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

    [Fact]
    public async Task InitializeAsync_FiresOnFeaturesRefreshedCallback()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateFeatures()));

        bool? callbackResult = null;

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = mockRepo,
            OnFeaturesRefreshed = success => callbackResult = success
        });

        await client.InitializeAsync();

        callbackResult.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshFeaturesAsync_FiresOnFeaturesRefreshedCallback()
    {
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateFeatures()));

        bool? callbackResult = null;

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = mockRepo,
            OnFeaturesRefreshed = success => callbackResult = success
        });

        await client.InitializeAsync();
        callbackResult = null;

        await client.RefreshFeaturesAsync();

        callbackResult.Should().BeTrue();
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

    /// <summary>
    /// A handler that throws has already failed; the refresh it is being told about has not. The
    /// features are in effect by then, and on the background path the exception would fault a task
    /// nobody awaits, so it would never be seen at all.
    /// </summary>
    [Fact]
    public async Task AThrowingRefreshHandlerDoesNotEscapeOrStopTheRefresh()
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
            OnFeaturesRefreshed = _ => throw new InvalidOperationException("the application's handler is broken")
        });

        await client.Awaiting(c => c.InitializeAsync()).Should().NotThrowAsync();

        client.GetFeatures().Should().ContainKey("dark-mode", "the features were applied before anyone was told");
    }

    /// <summary>
    /// Overlapping refreshes can finish out of order. The one that was overtaken is discarded rather
    /// than published, so reporting it would tell a listener that a feature set is in effect when
    /// nothing is evaluating against it.
    /// </summary>
    [Fact]
    public async Task ARefreshOvertakenByANewerOneIsNotReported()
    {
        using var firstCallMayFinish = new ManualResetEventSlim(false);
        using var firstCallStarted = new ManualResetEventSlim(false);

        var calls = 0;
        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();

        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstCallStarted.Set();
                    firstCallMayFinish.Wait(TimeSpan.FromSeconds(10));
                }

                return Task.FromResult(CreateFeatures());
            });

        var reported = 0;

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = mockRepo,
            OnFeaturesRefreshed = _ => Interlocked.Increment(ref reported)
        });

        var overtaken = Task.Run(() => client.RefreshFeaturesAsync());

        firstCallStarted.Wait(TimeSpan.FromSeconds(10));

        // Starts later, so it carries the higher sequence, and finishes first.
        await client.RefreshFeaturesAsync();

        firstCallMayFinish.Set();
        await overtaken;

        reported.Should().Be(1,
            "the newer refresh is the one in effect and the one that reported; the older carries " +
            "older definitions however late it comes back");
    }
}
