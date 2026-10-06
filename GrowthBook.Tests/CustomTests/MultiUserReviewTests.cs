using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.SSE;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using GrowthBook.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the defects review found in the multi-user mode change set. They are grouped here rather
/// than spread across the suites for the files they touch, because what they have in common is the
/// subject of the review rather than the component.
/// </summary>
public class MultiUserReviewTests
{
    private static Feature ExperimentFeature() => new Feature
    {
        DefaultValue = false,
        Rules = new List<FeatureRule>
        {
            new FeatureRule
            {
                Variations = new JArray(false, true),
                Coverage = 1d,
                Meta = new List<VariationMeta>
                {
                    new VariationMeta { Key = "0" },
                    new VariationMeta { Key = "1" }
                }
            }
        }
    };

    private static Dictionary<string, Feature> FeatureSet(bool value) =>
        new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = value } };

    // ---- SSE: findings 1 and 12 -------------------------------------------------------------

    private sealed class CountingHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private int _requests;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            return Task.FromResult(_respond(request));
        }
    }

    private sealed class HandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public HandlerFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new HttpClient(_handler, false);
    }

    private static SSEClient CreateSseClient(HttpMessageHandler handler) =>
        new SSEClient(Substitute.For<ILogger<SSEClient>>(), new HandlerFactory(handler),
            "https://example.test/stream", null, ConfiguredClients.ServerSentEventsApiClient);

    /// <summary>
    /// A 2xx response whose body ends immediately is a success as far as the status is concerned, so
    /// nothing counts it as a failure and nothing waits before trying again. Reconnecting straight
    /// away turns that into a request loop that stops only when the caller cancels.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task AnEmptySuccessfulStreamDoesNotReconnectInATightLoop()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
        });

        using var client = CreateSseClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await client.ConnectAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }

        handler.Requests.Should().BeLessThan(10,
            "because an endpoint that accepts and immediately closes must be backed off from, not hammered");
    }

    [Fact(Timeout = 40000)]
    public async Task AGoneResponseStopsInsteadOfRetrying()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone));

        using var client = CreateSseClient(handler);

        // Long enough that a client which retries would have waited out its backoff and sent a second
        // request well inside the window, rather than the window itself being what stops it.
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));

        await client.ConnectAsync(cancellation.Token);

        handler.Requests.Should().Be(1,
            "because 410 Gone means the subscription is over - retrying only repeats a request the server has refused");
        cancellation.IsCancellationRequested.Should().BeFalse(
            "and the client stopped on its own rather than being held open until something cancelled it");
    }

    // ---- FeatureRepository: finding 2 -------------------------------------------------------

    private sealed class SlowWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly SemaphoreSlim _entered = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public void Cancel() { }
        public bool WaitUntilEntered() => _entered.Wait(TimeSpan.FromSeconds(5));

        /// <summary>
        /// Gives another refresh a bounded chance to reach the worker, for asserting that none did.
        /// Counting attempts instead would read the number before the thread pool has run them.
        /// </summary>
        public bool AnotherRefreshStarted() => _entered.Wait(TimeSpan.FromSeconds(1));

        public void Release() => _release.Set();

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(5));

            return Task.FromResult<IDictionary<string, Feature>>(FeatureSet(true));
        }
    }

    /// <summary>
    /// A background refresh releases the lock while its request is still in flight, and the cache stays
    /// expired until that lands. Every caller arriving in that window would otherwise start its own.
    /// </summary>
    [Fact]
    public async Task CallersArrivingDuringARefreshJoinItRatherThanStartingAnother()
    {
        var worker = new SlowWorker();
        var cache = new InMemoryFeatureCache(0);
        await cache.RefreshWith(FeatureSet(false));

        var repository = new FeatureRepository(NullLogger<FeatureRepository>.Instance, cache, worker);

        var first = repository.GetFeatures();
        worker.WaitUntilEntered().Should().BeTrue("because the first call starts the refresh");

        await repository.GetFeatures();
        await repository.GetFeatures();

        worker.AnotherRefreshStarted().Should().BeFalse(
            "because the cache is still expired while the request is in flight, so without joining it every caller starts another");

        worker.Release();
        await first;

        worker.Attempts.Should().Be(1);
    }

    // ---- FeatureRefreshWorker: finding 13 ---------------------------------------------------

    [Fact]
    public async Task ASubscriberThrowingDoesNotTurnASuccessfulRefreshIntoAFailure()
    {
        var outcomes = new List<bool>();

        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://example.test",
            ClientKey = "key",
            OnFeaturesRefreshed = success =>
            {
                outcomes.Add(success);
                throw new InvalidOperationException("subscriber blew up");
            }
        };

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"features\":{\"flag\":{\"defaultValue\":true}}}", Encoding.UTF8, "application/json")
        });

        var cache = new InMemoryFeatureCache(60);
        var worker = new FeatureRefreshWorker(NullLogger<FeatureRefreshWorker>.Instance,
            new HandlerFactory(handler), config, cache);

        var features = await worker.RefreshCacheFromApi();

        features.Should().NotBeNull("because the refresh itself succeeded");
        outcomes.Should().Equal(new[] { true },
            "because one refresh is reported once - a throwing subscriber must not have it reported as failed as well");
    }

    // ---- GrowthBookClient: findings 3, 4, 5 and 11 -------------------------------------------

    private static GrowthBookClient CreateClient(Options options, IDictionary<string, Feature> features)
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(features));

        options.FeatureRepository = repository;

        return new GrowthBookClient(options);
    }

    [Fact]
    public async Task TheFeatureGetterHandsOutASnapshotRatherThanTheLiveMap()
    {
        var client = CreateClient(new Options { ClientKey = "key" }, FeatureSet(true));
        await client.InitializeAsync();

        var handedOut = client.GetFeatures();
        handedOut.Remove("flag");

        client.GetFeatures().Should().ContainKey("flag",
            "because a caller changing what they were given must not change what every other user evaluates against");
        client.IsOn("flag", new UserContext { Attributes = JObject.FromObject(new { id = "user-1" }) })
            .Should().BeTrue();
    }

    /// <summary>
    /// Evaluation hashes on the merged attributes - global, then user, then overrides - so an
    /// identifier can come from any of the three. Looking the sticky documents up from the user
    /// attributes alone finds nothing for the others and the user is bucketed afresh.
    /// </summary>
    [Fact]
    public async Task StickyDocumentsAreFoundWhenTheIdentifierComesFromGlobalAttributes()
    {
        var store = new InMemoryStickyBucketService();
        store.SaveAssignments(new StickyAssignmentsDocument(
            "tenantId",
            "tenant-7",
            new Dictionary<string, string> { ["flag__0"] = "1" }));

        var features = new Dictionary<string, Feature> { ["flag"] = ExperimentFeature() };
        features["flag"].Rules[0].HashAttribute = "tenantId";

        var client = CreateClient(new Options
        {
            ClientKey = "key",
            StickyBucketService = store,
            GlobalAttributes = JObject.FromObject(new { tenantId = "tenant-7" })
        }, features);

        await client.InitializeAsync();

        var result = client.EvalFeature("flag", new UserContext { Attributes = JObject.FromObject(new { id = "user-1" }) });

        result.ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "because the identifier the rule hashes on is in the global attributes, where the lookup has to look too");
        result.ExperimentResult.Key.Should().Be("1");
    }

    [Fact]
    public async Task StickyDocumentsAreFoundForAnExperimentPassedStraightToRun()
    {
        var store = new InMemoryStickyBucketService();
        store.SaveAssignments(new StickyAssignmentsDocument(
            "id",
            "user-1",
            new Dictionary<string, string> { ["inline-experiment__0"] = "1" }));

        var client = CreateClient(new Options { ClientKey = "key", StickyBucketService = store },
            new Dictionary<string, Feature>());

        await client.InitializeAsync();

        var result = client.Run(new Experiment
        {
            Key = "inline-experiment",
            Variations = new JArray(false, true),
            Coverage = 1d,
            Meta = new List<VariationMeta>
            {
                new VariationMeta { Key = "0" },
                new VariationMeta { Key = "1" }
            }
        }, new UserContext { Attributes = JObject.FromObject(new { id = "user-1" }) });

        result.StickyBucketUsed.Should().BeTrue(
            "because an inline experiment is in no feature rule, so nothing else names its identifier for the lookup");
        result.Key.Should().Be("1");
    }

    /// <summary>
    /// A repository whose reads finish when the test says so, so two overlapping loads can be made to
    /// come back in the opposite order to the one they started in.
    /// </summary>
    private sealed class GatedRepository : IGrowthBookFeatureRepository
    {
        private readonly List<TaskCompletionSource<IDictionary<string, Feature>>> _pending =
            new List<TaskCompletionSource<IDictionary<string, Feature>>>();

        public int ReadCount
        {
            get
            {
                lock (_pending)
                {
                    return _pending.Count;
                }
            }
        }

        public bool WaitForReads(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (ReadCount >= count)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }

        public void Finish(int index, IDictionary<string, Feature> features)
        {
            lock (_pending)
            {
                _pending[index].SetResult(features);
            }
        }

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            var pending = new TaskCompletionSource<IDictionary<string, Feature>>();

            lock (_pending)
            {
                _pending.Add(pending);
            }

            return pending.Task;
        }

        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) =>
            GetFeatures(options, cancellationToken);

        public void Cancel() { }
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;
    }

    /// <summary>
    /// Refreshes overlap, and the one that started first is holding older definitions however late it
    /// comes back. Publishing it on arrival rolls every user back until the next refresh.
    /// </summary>
    [Fact]
    public async Task AnOverlappingLoadThatFinishesLastDoesNotPublishItsOlderFeatures()
    {
        var repository = new GatedRepository();
        var client = new GrowthBookClient(new Options { ClientKey = "key", FeatureRepository = repository });

        var older = client.InitializeAsync();
        repository.WaitForReads(1).Should().BeTrue();

        var newer = client.RefreshFeaturesAsync();
        repository.WaitForReads(2).Should().BeTrue();

        repository.Finish(1, FeatureSet(true));
        await newer;

        repository.Finish(0, FeatureSet(false));
        await older;

        client.IsOn("flag", new UserContext { Attributes = JObject.FromObject(new { id = "user-1" }) })
            .Should().BeTrue("because the load that lost the race was already stale when it arrived");
    }

    // ---- ExperimentEvaluationProvider: finding 6 ---------------------------------------------

    /// <summary>
    /// The assignment is decided, and may already be persisted, before the callback runs. Letting the
    /// exception out leaves the user with the feature default while the store says otherwise.
    /// </summary>
    [Fact]
    public async Task ATrackingCallbackThatThrowsDoesNotChangeWhatTheUserIsAssigned()
    {
        var features = new Dictionary<string, Feature> { ["flag"] = ExperimentFeature() };

        var client = CreateClient(new Options
        {
            ClientKey = "key",
            TrackingCallback = (experiment, result) => throw new InvalidOperationException("tracking blew up")
        }, features);

        await client.InitializeAsync();

        var user = new UserContext { Attributes = JObject.FromObject(new { id = "user-1" }) };

        var evaluated = client.EvalFeature("flag", user);
        evaluated.ExperimentResult.Should().NotBeNull("because the assignment happened regardless of the callback");

        Action run = () => client.Run(new Experiment
        {
            Key = "inline",
            Variations = new JArray(false, true),
            Coverage = 1d
        }, user);

        run.Should().NotThrow("because a subscriber's failure is not the caller's to handle");
    }

    // ---- FeatureEvaluationProvider: findings 7 and 10 ----------------------------------------

    [Fact]
    public void AnOverrideAppliesToAFeatureThatIsNotInThePayload()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>(),
            ForcedFeatureValues = new Dictionary<string, JToken> { ["not-loaded"] = true }
        });

        var result = growthBook.EvalFeature("not-loaded");

        result.Source.Should().Be("override",
            "because an override is the caller overriding the payload, which includes a key the payload does not carry");
        result.On.Should().BeTrue();
    }

    [Fact]
    public void SubscribersAreToldAboutAForcedEvaluation()
    {
        var features = new Dictionary<string, Feature>
        {
            ["forced"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule> { new FeatureRule { Force = true } }
            }
        };

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = features
        });

        var seen = new List<ExperimentResult>();
        using var subscription = growthBook.Subscribe((experiment, result) => seen.Add(result));

        growthBook.EvalFeature("forced").On.Should().BeTrue();

        seen.Should().HaveCount(1, "because a forced rule is still an evaluation, and subscribers were told about it before");
        seen[0].InExperiment.Should().BeFalse();
    }

    // ---- IGrowthBook: finding 9 -------------------------------------------------------------

    /// <summary>
    /// The point is that this compiles: both methods are reached through the interface, which is how
    /// anyone resolving <see cref="IGrowthBook"/> from DI holds the SDK.
    /// </summary>
    [Fact]
    public void SubscriptionsAreReachableThroughTheInterface()
    {
        IGrowthBook growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(true)
        });

        using (growthBook)
        {
            using var sync = growthBook.Subscribe((experiment, result) => { });
            using var async = growthBook.SubscribeAsync((experiment, result) => Task.CompletedTask);

            sync.Should().NotBeNull();
            async.Should().NotBeNull();
        }
    }

    // ---- GrowthBookFactory: finding 8 -------------------------------------------------------

    [Fact]
    public void TheSharedConfigurationCarriesTheRequestConfiguration()
    {
        var context = new Context
        {
            ClientKey = "key",
            RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            StreamingRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer streaming-token" },
            OnFeaturesRefreshed = success => { },
            OnStreamingEventId = id => { }
        };

        var config = GrowthBookFactory.CreateSharedConfiguration(context);

        config.RequestHeaders.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer token"),
            "because dropping these makes the shared repository's feature requests unauthenticated");
        config.StreamingRequestHeaders.Should().Contain(new KeyValuePair<string, string>("Authorization", "Bearer streaming-token"));
        config.OnFeaturesRefreshed.Should().BeSameAs(context.OnFeaturesRefreshed);
        config.OnStreamingEventId.Should().BeSameAs(context.OnStreamingEventId);
    }
}
