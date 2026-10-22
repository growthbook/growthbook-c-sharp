using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Plugin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class GrowthBookTrackingPluginTests
    {
        private class CapturingHandler : HttpMessageHandler
        {
            private readonly CountdownEvent _latch;
            private readonly HttpStatusCode _statusCode;
            public List<JArray> Posts { get; } = new List<JArray>();

            public CapturingHandler(int expectedPosts = 1, HttpStatusCode statusCode = HttpStatusCode.OK)
            {
                _latch = new CountdownEvent(expectedPosts);
                _statusCode = statusCode;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content.ReadAsStringAsync();
                var json = JToken.Parse(body);
                if (json is JArray array)
                    lock (Posts) Posts.Add(array);

                if (_latch.CurrentCount > 0)
                    _latch.Signal();

                return new HttpResponseMessage(_statusCode);
            }

            public JArray WaitForPost(int timeoutSeconds = 5)
            {
                _latch.Wait(TimeSpan.FromSeconds(timeoutSeconds));
                lock (Posts) return Posts.Count > 0 ? Posts[0] : null;
            }

            public bool ReceivedNoPost(int timeoutMs = 500) =>
                !_latch.Wait(TimeSpan.FromMilliseconds(timeoutMs));

            public string LastUrl { get; private set; }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }

        private static Experiment MakeExperiment(string key) => new Experiment { Key = key };

        private static ExperimentResult MakeExperimentResult(int variation = 0) => new ExperimentResult
        {
            VariationId = variation,
            HashAttribute = "id",
            HashValue = $"u-{variation}",
        };

        private static FeatureResult MakeFeatureResult(string value = "v") => new FeatureResult
        {
            Value = new JValue(value),
            Source = FeatureResult.SourceId.DefaultValue,
        };

        [Fact]
        public void FlushesWhenBatchSizeReached()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 2,
                BatchTimeout = TimeSpan.FromSeconds(30),
            });
            plugin.Init();

            plugin.OnExperimentViewed(MakeExperiment("exp1"), MakeExperimentResult(0), null);
            plugin.OnExperimentViewed(MakeExperiment("exp2"), MakeExperimentResult(1), null);

            var events = handler.WaitForPost();
            events.Should().NotBeNull("should flush when batch size is reached");
            events.Count.Should().Be(2);

            var experimentIds = events
                .Select(e => e["properties"]?["experimentId"]?.Value<string>())
                .ToList();
            experimentIds.Should().Contain("exp1");
            experimentIds.Should().Contain("exp2");

            plugin.Close();
        }

        [Fact]
        public void FlushesWhenTimerFires()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(200),
            });
            plugin.Init();

            plugin.OnFeatureEvaluated("flag1", MakeFeatureResult(), null);

            var events = handler.WaitForPost(timeoutSeconds: 5);
            events.Should().NotBeNull("timer-based flush should fire within 5s");
            events.Count.Should().Be(1);
            events[0]["event_name"]?.Value<string>().Should().Be(TrackingEvent.EventFeatureEvaluated);
            events[0]["properties"]?["feature"]?.Value<string>().Should().Be("flag1");

            plugin.Close();
        }

        [Fact]
        public void CloseFlushesRemainingEvents()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 100,
                BatchTimeout = TimeSpan.FromSeconds(60),
            });
            plugin.Init();

            plugin.OnExperimentViewed(MakeExperiment("exp"), MakeExperimentResult(), null);
            plugin.Close();

            var events = handler.WaitForPost();
            events.Should().NotBeNull("Close() should flush remaining events");
            events.Count.Should().Be(1);
        }

        [Fact]
        public void CloseIsIdempotent()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
            });

            var act = () =>
            {
                plugin.Close();
                plugin.Close();
            };

            act.Should().NotThrow();
        }

        [Fact]
        public void NullClientKeyDisablesPlugin()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = null,
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();

            plugin.OnExperimentViewed(MakeExperiment("exp"), MakeExperimentResult(), null);
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);
            plugin.Close();

            handler.ReceivedNoPost().Should().BeTrue("null clientKey must disable the plugin");
        }

        [Fact]
        public void EmptyClientKeyDisablesPlugin()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();

            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);
            plugin.Close();

            handler.ReceivedNoPost().Should().BeTrue("empty clientKey must disable the plugin");
        }

        [Fact]
        public void HttpErrorDoesNotThrow()
        {
            var handler = new CapturingHandler(statusCode: HttpStatusCode.InternalServerError);
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();

            var act = () =>
            {
                plugin.OnExperimentViewed(MakeExperiment("exp"), MakeExperimentResult(), null);
                handler.WaitForPost();
                plugin.Close();
            };

            act.Should().NotThrow();
        }

        [Fact]
        public void IngestorHostTrailingSlashIsStripped()
        {
            var config = new TrackingPluginConfig
            {
                IngestorHost = "https://example.test/",
                ClientKey = "k",
            };

            config.ResolvedIngestorHost().Should().Be("https://example.test");
            config.ResolvedIngestorHost().Should().NotEndWith("/");
        }

        /// <summary>
        /// The literal, not the constant compared with itself - the point is that the default is the
        /// host the rest of GrowthBook uses. Events posted anywhere else are accepted and dropped, so a
        /// wrong default fails silently and the only symptom is an empty warehouse.
        /// </summary>
        [Fact]
        public void DefaultIngestorHostIsUsedWhenNotSet()
        {
            var config = new TrackingPluginConfig { ClientKey = "k" };

            config.ResolvedIngestorHost().Should().Be("https://us-east-1.gb-ingest.com");
            TrackingPluginConfig.DefaultIngestorHost.Should().Be("https://us-east-1.gb-ingest.com");
        }

        [Fact]
        public void SdkMetadataVersionIsNotEmpty()
        {
            SdkMetadata.Version.Should().NotBeNullOrEmpty();
            SdkMetadata.Version.Should().NotBe("unknown");
        }

        [Fact]
        public void AttributesAreIncludedInEvent()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();

            var attrs = new JObject { ["id"] = "u1", ["plan"] = "pro" };
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), attrs);

            var events = handler.WaitForPost();
            events.Should().NotBeNull();

            var eventAttrs = events[0]["attributes"] as JObject;
            eventAttrs.Should().NotBeNull("attributes should be present in event");
            eventAttrs["id"]?.Value<string>().Should().Be("u1");
            eventAttrs["plan"]?.Value<string>().Should().Be("pro");

            plugin.Close();
        }

        [Fact]
        public void SdkAttributesAreMergedIntoEvent()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);

            var events = handler.WaitForPost();
            events.Should().NotBeNull();

            var attrs = events[0]["attributes"] as JObject;
            attrs.Should().NotBeNull();
            attrs["sdk_language"]?.Value<string>().Should().Be(SdkMetadata.Language);
            attrs["sdk_version"]?.Value<string>().Should().Be(SdkMetadata.Version);

            plugin.Close();
        }

        [Fact]
        public void BodyIsPlainJsonArray()
        {
            var handler = new CapturingHandler();
            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "sdk-test",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);

            var events = handler.WaitForPost();
            events.Should().NotBeNull();
            events.Should().BeOfType<JArray>("body must be a plain JSON array, not a wrapped object");

            plugin.Close();
        }

        [Fact]
        public void PostUrlUsesTrackEndpointWithClientKey()
        {
            string capturedUrl = null;
            var latch = new CountdownEvent(1);

            var handler = new CapturingUrlHandler(url =>
            {
                capturedUrl = url;
                latch.Signal();
            });

            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                IngestorHost = "https://ingest.example.com",
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });
            plugin.Init();
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);

            latch.Wait(TimeSpan.FromSeconds(5));
            capturedUrl.Should().Be("https://ingest.example.com/track?client_key=k");

            plugin.Close();
        }

        /// <summary>
        /// A send that is already under way is still the caller's data. Once a batch leaves the buffer,
        /// the buffer looks empty, so nothing else can notice it is missing.
        /// </summary>
        [Fact]
        public void CloseWaitsForABatchAlreadyOnItsWay()
        {
            var handler = new SlowHandler(TimeSpan.FromMilliseconds(300));

            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });

            plugin.Init();
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);

            plugin.Close();

            handler.CompletedSends.Should().Be(1,
                "Close has to wait for the batch it can no longer see, otherwise a process that exits " +
                "straight after disposing GrowthBook loses it");
        }

        /// <summary>
        /// An event arriving after the final drain would sit in a buffer nothing will flush again. It
        /// has to be refused, and the refusal has to be visible - otherwise the event simply disappears.
        /// </summary>
        /// <remarks>
        /// The guard lives inside the enqueue lock, which is what closes the window between the final
        /// drain and the flag being set. That interleaving cannot be forced from a test, so what is
        /// asserted here is the guard running at all.
        /// </remarks>
        [Fact]
        public void AnEventArrivingAfterCloseIsRefusedRatherThanBuffered()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var logger = new CapturingLogger();

            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            }, logger);

            plugin.Init();
            plugin.OnFeatureEvaluated("before", MakeFeatureResult(), null);
            plugin.Close();

            plugin.OnFeatureEvaluated("after", MakeFeatureResult(), null);

            logger.Messages.Should().Contain(m => m.Contains("the plugin is closed"),
                "an event that cannot be sent must not be accepted in silence");

            lock (handler.Posts)
            {
                handler.Posts.SelectMany(p => p).Should().HaveCount(1, "only the event sent before the close");
            }
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public List<string> Messages { get; } = new List<string>();

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }

        /// <summary>
        /// A batch holds a request, its content and a response. Left undisposed they accumulate for as
        /// long as the caller's HttpClient lives, which for an injected one is the life of the process.
        /// </summary>
        [Fact]
        public void EveryBatchReleasesItsHttpResources()
        {
            var handler = new DisposalTrackingHandler();

            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
            });

            plugin.Init();
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);
            plugin.Close();

            handler.ResponseStream.Should().NotBeNull("a send happened");
            handler.ResponseStream.WasDisposed.Should().BeTrue("the response is disposed once it has been read");

            Action readRequestBody = () => handler.LastRequest.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            readRequestBody.Should().Throw<ObjectDisposedException>("and so are the request and its content");
        }

        /// <summary>
        /// A slow or unreachable ingestor must cost a bounded amount of memory, not an unbounded queue
        /// of batches waiting behind it.
        /// </summary>
        [Fact]
        public void BatchesStopBeingQueuedOnceTheLimitIsReached()
        {
            using var gate = new ManualResetEventSlim(false);

            var handler = new GatedHandler(gate);

            var plugin = new GrowthBookTrackingPlugin(new TrackingPluginConfig
            {
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1,
                MaxPendingBatches = 2,
            });

            plugin.Init();

            for (var i = 0; i < 20; i++)
            {
                plugin.OnFeatureEvaluated($"flag-{i}", MakeFeatureResult(), null);
            }

            gate.Set();
            plugin.Close();

            handler.Sends.Should().BeLessOrEqualTo(3,
                "two batches may be waiting and one more can slip in as the first completes - but not twenty");
        }

        /// <summary>
        /// A feature is evaluated wherever it is checked, so in a hot path the same fact would reach
        /// the ingestor on every request.
        /// </summary>
        [Fact]
        public void TheSameEvaluationIsOnlySentOnce()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = NewPlugin(handler);

            for (var i = 0; i < 50; i++)
            {
                plugin.OnFeatureEvaluated("flag", MakeFeatureResult(), null);
            }

            plugin.Close();

            SentEvents(handler).Should().Be(1, "fifty evaluations of the same flag are one fact");
        }

        /// <summary>
        /// Deduplication is on what happened, not on which flag it happened to. A flag that changes
        /// value has something new to report.
        /// </summary>
        [Fact]
        public void TheSameFeatureResolvingDifferentlyIsSentAgain()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = NewPlugin(handler);

            plugin.OnFeatureEvaluated("flag", MakeFeatureResult("on"), null);
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult("on"), null);
            plugin.OnFeatureEvaluated("flag", MakeFeatureResult("off"), null);

            plugin.Close();

            SentEvents(handler).Should().Be(2);
        }

        /// <summary>
        /// Attributes are left out of the key by default, because an instance belongs to one user.
        /// A caller whose instance serves more than one identity names the attributes that separate them.
        /// </summary>
        [Fact]
        public void NamedAttributesSeparateOtherwiseIdenticalEvents()
        {
            var withoutNames = new CapturingHandler(expectedPosts: 1);
            var shared = NewPlugin(withoutNames);

            shared.OnFeatureEvaluated("flag", MakeFeatureResult(), new JObject { ["id"] = "user-1" });
            shared.OnFeatureEvaluated("flag", MakeFeatureResult(), new JObject { ["id"] = "user-2" });
            shared.Close();

            SentEvents(withoutNames).Should().Be(1, "by default the identity is not part of the key");

            var withNames = new CapturingHandler(expectedPosts: 1);
            var separated = NewPlugin(withNames, c => c.DedupeKeyAttributes = new[] { "id" });

            separated.OnFeatureEvaluated("flag", MakeFeatureResult(), new JObject { ["id"] = "user-1" });
            separated.OnFeatureEvaluated("flag", MakeFeatureResult(), new JObject { ["id"] = "user-2" });
            separated.Close();

            SentEvents(withNames).Should().Be(2, "once the attribute is named, the two users are two events");
        }

        /// <summary>
        /// The cache is bounded, so it forgets. Least-recently-used, so what it forgets first is what
        /// has not been seen for longest rather than whatever happened to arrive first.
        /// </summary>
        [Fact]
        public void TheLeastRecentlySeenEventIsForgottenFirst()
        {
            var handler = new CapturingHandler(expectedPosts: 1);
            var plugin = NewPlugin(handler, c => c.DedupeCacheSize = 2);

            plugin.OnFeatureEvaluated("a", MakeFeatureResult(), null);
            plugin.OnFeatureEvaluated("b", MakeFeatureResult(), null);

            // Seeing "a" again moves it back to the front, so "b" becomes the oldest.
            plugin.OnFeatureEvaluated("a", MakeFeatureResult(), null);
            plugin.OnFeatureEvaluated("c", MakeFeatureResult(), null);

            plugin.OnFeatureEvaluated("a", MakeFeatureResult(), null);
            plugin.OnFeatureEvaluated("b", MakeFeatureResult(), null);

            plugin.Close();

            SentEvents(handler).Should().Be(4,
                "a, b and c the first time, then b again once it had been evicted - but not a, which was kept alive by being seen");
        }

        private static GrowthBookTrackingPlugin NewPlugin(HttpMessageHandler handler, Action<TrackingPluginConfig> configure = null)
        {
            var config = new TrackingPluginConfig
            {
                ClientKey = "k",
                HttpClient = new HttpClient(handler),
                BatchSize = 1000,
            };

            configure?.Invoke(config);

            var plugin = new GrowthBookTrackingPlugin(config);

            plugin.Init();

            return plugin;
        }

        private static int SentEvents(CapturingHandler handler)
        {
            lock (handler.Posts)
            {
                return handler.Posts.Sum(p => p.Count);
            }
        }

        private sealed class SlowHandler : HttpMessageHandler
        {
            private readonly TimeSpan _delay;
            private int _completed;

            public SlowHandler(TimeSpan delay) => _delay = delay;

            public int CompletedSends => Volatile.Read(ref _completed);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(_delay, cancellationToken);

                Interlocked.Increment(ref _completed);

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class GatedHandler : HttpMessageHandler
        {
            private readonly ManualResetEventSlim _gate;
            private int _sends;

            public GatedHandler(ManualResetEventSlim gate) => _gate = gate;

            public int Sends => Volatile.Read(ref _sends);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _sends);

                _gate.Wait(TimeSpan.FromSeconds(10));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class DisposalTrackingHandler : HttpMessageHandler
        {
            public TrackingStream ResponseStream { get; private set; }

            public HttpRequestMessage LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                ResponseStream = new TrackingStream();

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(ResponseStream)
                });
            }
        }

        private sealed class TrackingStream : System.IO.MemoryStream
        {
            public bool WasDisposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                WasDisposed = true;

                base.Dispose(disposing);
            }
        }

        private class CapturingUrlHandler : HttpMessageHandler
        {
            private readonly Action<string> _onRequest;

            public CapturingUrlHandler(Action<string> onRequest) => _onRequest = onRequest;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _onRequest(request.RequestUri.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
