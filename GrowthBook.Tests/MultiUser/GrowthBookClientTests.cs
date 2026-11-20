using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.MultiUser;
using GrowthBook.Evaluation;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.MultiUser;

public class GrowthBookClientTests
{
    private static IDictionary<string, Feature> CreateFeatures() => new Dictionary<string, Feature>
    {
        ["dark-mode"] = new Feature
        {
            DefaultValue = JToken.FromObject(false),
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Variations = JArray.FromObject(new[] { false, true }),
                    HashAttribute = "id",
                    Coverage = 1.0,
                    Weights = new List<double> { 0.5, 0.5 }
                }
            }
        }
    };

    private static GrowthBookClient CreateClient()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateFeatures()));

        return new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test", LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = mockRepo
        });
    }

    [Fact]
    public async Task EvalFeature_ConcurrentRequests_DoNotMixUserAttributes()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        var expected = Enumerable.Range(1, 100)
            .ToDictionary(
                i => $"user-{i}",
                i => client.EvalFeature("dark-mode",
                    new UserContext { Attributes = JObject.Parse($"{{\"id\": \"user-{i}\"}}") }
                ).On
            );

        var tasks = Enumerable.Range(1, 100)
            .Select(i => Task.Run(() =>
            {
                var userId = $"user-{i}";
                var result = client.EvalFeature("dark-mode",
                    new UserContext { Attributes = JObject.Parse($"{{\"id\": \"{userId}\"}}") });

                return (userId, result.On);
            }));

        var results = Task.WhenAll(tasks).Result;

        foreach (var (userId, actualValue) in results)
        {
            actualValue.Should().Be(expected[userId],
                because: $"{userId} should always get their own result, not another user's");
        }
    }

    [Fact]
    public async Task IsOn_ReturnsDeterministicResult_ForSameUser()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        var userCtx = new UserContext { Attributes = JObject.Parse("{\"id\": \"user-42\"}") };

        var first = client.IsOn("dark-mode", userCtx);
        var second = client.IsOn("dark-mode", userCtx);
        var third = client.IsOn("dark-mode", userCtx);

        second.Should().Be(first);
        third.Should().Be(first);
    }

    [Fact]
    public async Task EvalFeature_UnknownFeature_ReturnsOff()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        var result = client.EvalFeature("non-existent-feature",
            new UserContext { Attributes = JObject.Parse("{\"id\": \"user-1\"}") });

        result.On.Should().BeFalse();
        result.Source.Should().Be(FeatureResult.SourceId.UnknownFeature);
    }

    [Fact]
    public async Task InitializeAsync_WithNullFeatures_DoesNotCrash()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(null));

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test", LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = mockRepo
        });

        await client.InitializeAsync();

        var result = client.IsOn("dark-mode", new UserContext
        {
            Attributes = JObject.Parse("{\"id\": \"user-1\"}")
        });

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EvalFeature_GlobalAttributes_MergedWithUserAttributes_UserWins()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateFeatures()));

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = mockRepo,
            GlobalAttributes = JObject.Parse("{\"id\": \"global-user\"}")
        });

        await client.InitializeAsync();

        // User attribute "id" overrides global "id"
        var userCtx = new UserContext { Attributes = JObject.Parse("{\"id\": \"user-42\"}") };
        var withOverride = client.EvalFeature("dark-mode", userCtx);

        // No user attribute — falls back to global "id"
        var globalCtx = new UserContext { Attributes = new JObject() };
        var withGlobal = client.EvalFeature("dark-mode", globalCtx);

        // Same user id should give same result regardless of where it came from
        withOverride.On.Should().Be(client.EvalFeature("dark-mode",
            new UserContext { Attributes = JObject.Parse("{\"id\": \"user-42\"}") }).On);

        withGlobal.On.Should().Be(client.EvalFeature("dark-mode",
            new UserContext { Attributes = JObject.Parse("{\"id\": \"global-user\"}") }).On);
    }

    [Fact]
    public async Task SetGlobalForcedVariations_ForcesVariationForAllUsers()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        client.SetGlobalForcedVariations(new Dictionary<string, int>
        {
            ["dark-mode"] = 1
        });

        var result = client.EvalFeature("dark-mode",
            new UserContext { Attributes = JObject.Parse("{\"id\": \"any-user\"}") });

        result.On.Should().BeTrue();
    }

    /// <summary>
    /// A public refresh notification waits on the choice between #89 and #101, so neither options type
    /// exposes one in the meantime.
    /// </summary>
    [Fact]
    public void NoRefreshCallbackIsPublicYet()
    {
        foreach (var type in new[] { typeof(Options), typeof(GrowthBookConfigurationOptions) })
        {
            type.GetProperty("OnFeaturesRefreshed").Should().BeNull($"{type.Name} should not offer a public refresh callback yet");
            type.GetProperty("OnFeaturesStreamed").Should().BeNull($"{type.Name} keeps the streaming hook internal");
        }
    }

    /// <summary>
    /// With no documents supplied, the client loads them from the service under the keys evaluation saves under.
    /// An empty hash attribute means "id" there, so a saved assignment has to be found for it too.
    /// </summary>
    [Fact]
    public async Task ASavedAssignmentIsFoundWhenTheHashAttributeIsEmpty()
    {
        var value = await StickyValueAsync(hashAttribute: "", id: JToken.FromObject("user-1"));

        value.Should().Be(1, "the user was saved in variation 1, which the weights alone would never pick");
    }

    /// <summary>
    /// Evaluation writes numbers the way JavaScript does, so the client has to look a numeric id up the same way.
    /// </summary>
    [Fact]
    public async Task ASavedAssignmentIsFoundForANumericId()
    {
        var value = await StickyValueAsync(hashAttribute: "id", id: JToken.FromObject(1e21));

        value.Should().Be(1, "the user was saved in variation 1, which the weights alone would never pick");
    }

    private static async Task<int> StickyValueAsync(string hashAttribute, JToken id)
    {
        var attributes = new JObject { ["id"] = id };
        var (attribute, hashValue) = global::GrowthBook.Extensions.JsonExtensions.GetHashAttributeAndValue(attributes, hashAttribute);

        var service = new global::GrowthBook.Services.InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument(attribute, hashValue, new Dictionary<string, string> { ["exp__0"] = "1" }));

        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>
            {
                ["flag"] = new Feature
                {
                    DefaultValue = JToken.FromObject(-1),
                    Rules = new List<FeatureRule>
                    {
                        new FeatureRule
                        {
                            Key = "exp",
                            HashAttribute = hashAttribute,
                            Variations = new JArray(0, 1),
                            Weights = new List<double> { 1, 0 },
                            Coverage = 1,
                            Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
                        }
                    }
                }
            }));

        using var client = new GrowthBookClient(new Options
        {
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            StickyBucketService = service
        });

        await client.InitializeAsync();

        return client.GetFeature("flag", -1, new UserContext { Attributes = attributes });
    }

    /// <summary>
    /// Without documents of its own, each call loads a separate copy, so a lock on the documents doesn't span
    /// calls. Parallel checks for one user must still each add their assignment to the saved document.
    /// </summary>
    [Fact]
    public async Task ParallelChecksForOneUserKeepEverySavedAssignment()
    {
        const int ExperimentCount = 30;

        var features = Enumerable.Range(0, ExperimentCount).ToDictionary(
            i => $"flag-{i}",
            i => new Feature
            {
                DefaultValue = JToken.FromObject(-1),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Key = $"exp-{i}",
                        Variations = new JArray(0, 1),
                        Weights = new List<double> { 0.5, 0.5 },
                        Coverage = 1,
                        Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
                    }
                }
            });

        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

        var service = new SlowStickyBucketService();

        using var client = new GrowthBookClient(new Options
        {
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            StickyBucketService = service
        });

        await client.InitializeAsync();

        var user = new UserContext { Attributes = new JObject { ["id"] = "user-1" } };

        Parallel.ForEach(features.Keys, new ParallelOptions { MaxDegreeOfParallelism = 16 }, key => client.GetFeature(key, -1, user));

        service.GetAssignments("id", "user-1").Assignments.Should().HaveCount(ExperimentCount,
            "every check saved an assignment, and none may be lost to another check's save");
    }

    /// <summary>
    /// Widens the gap between reading a document and saving it, as a remote store would.
    /// </summary>
    private sealed class SlowStickyBucketService : global::GrowthBook.Services.IStickyBucketService
    {
        private readonly global::GrowthBook.Services.InMemoryStickyBucketService _inner = new global::GrowthBook.Services.InMemoryStickyBucketService();

        public IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes) => _inner.GetAllAssignments(attributes);

        public StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue)
        {
            var document = _inner.GetAssignments(attributeName, attributeValue);
            Thread.Sleep(1);
            return document;
        }

        public void SaveAssignments(StickyAssignmentsDocument document)
        {
            Thread.Sleep(1);
            _inner.SaveAssignments(document);
        }
    }

    /// <summary>
    /// The set a request carries to deduplicate exposures is shared by its parallel flag checks. Each exposure
    /// has to be reported once, and none of them may break an evaluation.
    /// </summary>
    [Fact]
    public async Task ParallelChecksInOneRequestReportEachExposureOnce()
    {
        const int ExperimentCount = 100;

        var features = Enumerable.Range(0, ExperimentCount).ToDictionary(
            i => $"flag-{i}",
            i => new Feature
            {
                DefaultValue = JToken.FromObject(-1),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule { Key = $"exp-{i}", Variations = new JArray(0, 1), Weights = new List<double> { 0, 1 }, Coverage = 1 }
                }
            });

        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

        var reported = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        using var client = new GrowthBookClient(new Options
        {
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            TrackingCallback = (experiment, result) => reported.AddOrUpdate(experiment.Key, 1, (_, count) => count + 1)
        });

        await client.InitializeAsync();

        for (var round = 0; round < 20; round++)
        {
            reported.Clear();

            var request = new UserContext
            {
                Attributes = new JObject { ["id"] = $"user-{round}" },
                TrackedExperiments = new HashSet<string>()
            };

            var values = new System.Collections.Concurrent.ConcurrentBag<int>();

            // Every flag checked twice, all at once, within the one request.
            Parallel.ForEach(features.Keys.Concat(features.Keys), new ParallelOptions { MaxDegreeOfParallelism = 16 },
                key => values.Add(client.GetFeature(key, -1, request)));

            values.Should().OnlyContain(value => value == 1, "no evaluation may fall back to the default");
            reported.Should().HaveCount(ExperimentCount);
            reported.Values.Should().OnlyContain(count => count == 1, "each exposure is reported once per request");
        }
    }

    /// <summary>
    /// A repository that answers without features is a failed load, not an empty one, and the caller has to be
    /// able to tell the two apart.
    /// </summary>
    [Fact]
    public async Task InitializeAsyncReportsWhetherAnythingWasLoaded()
    {
        var empty = Substitute.For<IGrowthBookFeatureRepository>();
        empty.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(null));

        using (var client = new GrowthBookClient(new Options { LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = empty }))
        {
            (await client.InitializeAsync()).Success.Should().BeFalse("the repository returned no features");
        }

        using (var client = CreateClient())
        {
            var result = await client.InitializeAsync();

            result.Success.Should().BeTrue();
            result.FeatureCount.Should().Be(CreateFeatures().Count);
        }
    }

    /// <summary>
    /// The refresh an application calls to recover reports whether it worked, and a failed one keeps the
    /// features already in use rather than dropping them.
    /// </summary>
    [Fact]
    public async Task RefreshFeaturesAsyncReportsWhetherAnythingWasLoaded()
    {
        var calls = 0;
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref calls) == 2 ? null : CreateFeatures()));

        using var client = new GrowthBookClient(new Options { LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = repository });

        await client.InitializeAsync();

        (await client.RefreshFeaturesAsync()).Success.Should().BeFalse("the repository returned no features");
        client.GetFeatures().Should().ContainKey("dark-mode", "a failed refresh leaves the features in use in place");

        var result = await client.RefreshFeaturesAsync();

        result.Success.Should().BeTrue();
        result.FeatureCount.Should().Be(CreateFeatures().Count);
    }

    /// <summary>
    /// A repository that relays refreshes pushes them into the client, as it does into GrowthBook instances, so a
    /// client sharing it sees new definitions without loading them itself.
    /// </summary>
    [Fact]
    public async Task AClientReceivesTheRefreshesItsRepositoryRelays()
    {
        var repository = new RelayingRepository(FlagSet(false));

        using var client = new GrowthBookClient(new Options { LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = repository });
        await client.InitializeAsync();

        repository.Push(1, FlagSet(true));

        client.IsOn("flag", new UserContext()).Should().BeTrue("the relayed refresh is newer than what the client loaded");
    }

    /// <summary>
    /// Relayed refreshes can overtake each other on the way. One older than a refresh already installed is dropped.
    /// </summary>
    [Fact]
    public async Task AnOlderRefreshRelayedLastIsDropped()
    {
        var repository = new RelayingRepository(FlagSet(false));

        using var client = new GrowthBookClient(new Options { LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = repository });
        await client.InitializeAsync();

        repository.Push(2, FlagSet(true));
        repository.Push(1, FlagSet(false));

        client.IsOn("flag", new UserContext()).Should().BeTrue("the refresh at version 1 is older than the one already installed");
    }

    [Fact]
    public void DisposingTheClientStopsTheRelay()
    {
        var repository = new RelayingRepository(FlagSet(false));

        new GrowthBookClient(new Options { LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = repository }).Dispose();

        repository.Subscribers.Should().Be(0, "a disposed client must not stay subscribed to a repository it does not own");
    }

    private static IDictionary<string, Feature> FlagSet(bool on) =>
        new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = JToken.FromObject(on) } };

    private sealed class RelayingRepository : IGrowthBookFeatureRepository, global::GrowthBook.Api.IFeatureRefreshSource
    {
        private readonly List<Action<global::GrowthBook.Api.FeatureRefresh>> _handlers = new List<Action<global::GrowthBook.Api.FeatureRefresh>>();
        private readonly IDictionary<string, Feature> _initial;

        public RelayingRepository(IDictionary<string, Feature> initial) => _initial = initial;

        public int Subscribers
        {
            get
            {
                lock (_handlers)
                {
                    return _handlers.Count;
                }
            }
        }

        public void Push(long version, IDictionary<string, Feature> features)
        {
            Action<global::GrowthBook.Api.FeatureRefresh>[] handlers;

            lock (_handlers)
            {
                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(new global::GrowthBook.Api.FeatureRefresh(version, features));
            }
        }

        public IDisposable SubscribeToRefresh(Action<global::GrowthBook.Api.FeatureRefresh> handler)
        {
            lock (_handlers)
            {
                _handlers.Add(handler);
            }

            return new Unsubscriber(() =>
            {
                lock (_handlers)
                {
                    _handlers.Remove(handler);
                }
            });
        }

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) =>
            Task.FromResult(_initial);

        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) =>
            Task.FromResult(_initial);

        public void Cancel() { }
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;

        private sealed class Unsubscriber : IDisposable
        {
            private Action _unsubscribe;

            public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;

            public void Dispose()
            {
                _unsubscribe?.Invoke();
                _unsubscribe = null;
            }
        }
    }

    [Fact]
    public async Task GetFeatures_ReturnsLoadedFeatures()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        var features = client.GetFeatures();

        features.Should().ContainKey("dark-mode");
    }

    [Fact]
    public async Task SetTrackingCallback_IsInvokedOnExperimentEval()
    {
        using var client = CreateClient();
        await client.InitializeAsync();

        Experiment trackedExperiment = null;
        client.SetTrackingCallback((exp, result) => trackedExperiment = exp);

        client.EvalFeature("dark-mode",
            new UserContext { Attributes = JObject.Parse("{\"id\": \"user-1\"}") });

        trackedExperiment.Should().NotBeNull();
        trackedExperiment.Key.Should().Be("dark-mode");
    }

    [Fact]
    public async Task GetFeature_WithAJsonNullValue_ReturnsTheFallback()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>
            {
                ["nullable"] = new Feature { DefaultValue = JValue.CreateNull() }
            }));

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test", LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = mockRepo
        });

        await client.InitializeAsync();

        client.GetFeature("nullable", "fallback", new UserContext { Attributes = JObject.Parse("{\"id\": \"user-1\"}") })
            .Should().Be("fallback", "a JSON null is no value, just as a missing one is");
    }

    private static IDictionary<string, Feature> SavedGroupFeature() => new Dictionary<string, Feature>
    {
        ["beta-only"] = new Feature
        {
            DefaultValue = JToken.FromObject(false),
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Condition = JObject.Parse("{\"id\":{\"$inGroup\":\"beta\"}}"),
                    Force = JToken.FromObject(true)
                }
            }
        }
    };

    private static GrowthBookClient ClientWithSavedGroups(JObject savedGroups)
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SavedGroupFeature()));

        return new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            SavedGroups = savedGroups
        });
    }

    /// <summary>
    /// A condition naming a saved group has nothing to resolve it against unless the definitions are
    /// supplied. Without them the rule simply does not match, so the feature targets correctly in the
    /// dashboard and is silently off for everyone here.
    /// </summary>
    [Fact]
    public async Task ASavedGroupConditionMatchesWhenTheGroupsAreSupplied()
    {
        using var client = ClientWithSavedGroups(JObject.Parse("{\"beta\":[\"user-1\"]}"));

        await client.InitializeAsync();

        client.IsOn("beta-only", new UserContext { Attributes = new JObject { ["id"] = "user-1" } })
            .Should().BeTrue("user-1 is in the beta group");

        client.IsOn("beta-only", new UserContext { Attributes = new JObject { ["id"] = "user-2" } })
            .Should().BeFalse("user-2 is not");
    }

    [Fact]
    public async Task SavedGroupsCanBeReplacedAfterTheClientIsBuilt()
    {
        using var client = ClientWithSavedGroups(JObject.Parse("{\"beta\":[]}"));

        await client.InitializeAsync();

        var user = new UserContext { Attributes = new JObject { ["id"] = "user-1" } };

        client.IsOn("beta-only", user).Should().BeFalse();

        client.SetSavedGroups(JObject.Parse("{\"beta\":[\"user-1\"]}"));

        client.IsOn("beta-only", user).Should().BeTrue();
    }
}
