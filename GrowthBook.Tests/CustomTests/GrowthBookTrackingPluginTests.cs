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

        private static FeatureResult MakeFeatureResult() => new FeatureResult
        {
            Value = new JValue("v"),
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

        [Fact]
        public void DefaultIngestorHostIsUsedWhenNotSet()
        {
            var config = new TrackingPluginConfig { ClientKey = "k" };
            config.ResolvedIngestorHost().Should().Be(TrackingPluginConfig.DefaultIngestorHost);
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
