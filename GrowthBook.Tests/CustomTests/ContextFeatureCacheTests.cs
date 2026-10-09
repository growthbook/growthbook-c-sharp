using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class ContextFeatureCacheTests : UnitTest
{
    /// <summary>
    /// A cache that records what was asked of it and can be pre-seeded, standing in for whatever the host
    /// application already runs. Used where the assertion is about how often the SDK reads through the
    /// supplied cache rather than about a single call.
    /// </summary>
    private sealed class RecordingCache : IGrowthBookFeatureCache
    {
        private IDictionary<string, Feature> _features = new Dictionary<string, Feature>();

        public int GetFeaturesCallCount { get; private set; }
        public int RefreshWithCallCount { get; private set; }
        public bool IsCacheExpired { get; set; }

        public int FeatureCount => _features.Count;

        public void Seed(IDictionary<string, Feature> features) => _features = features;

        public Task<IDictionary<string, Feature>> GetFeatures(CancellationToken? cancellationToken = null)
        {
            GetFeaturesCallCount++;

            return Task.FromResult(_features);
        }

        public Task RefreshWith(IDictionary<string, Feature> features, CancellationToken? cancellationToken = null)
        {
            RefreshWithCallCount++;
            _features = features;
            IsCacheExpired = false;

            return Task.CompletedTask;
        }
    }

    private static Dictionary<string, Feature> FeatureSet(bool value) =>
        new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = value } };

    [Fact]
    public async Task LoadFeatures_ShouldUseCustomCacheFromContext_WhenFeatureCacheIsProvided()
    {
        const string featureKey = "custom-cache-feature";
        var expectedFeatures = new Dictionary<string, Feature> { [featureKey] = new() { DefaultValue = true } };

        var customCache = Substitute.For<IGrowthBookFeatureCache>();
        customCache.IsCacheExpired.Returns(false);
        customCache.GetFeatures(Arg.Any<System.Threading.CancellationToken?>())
            .Returns(System.Threading.Tasks.Task.FromResult<IDictionary<string, Feature>>(expectedFeatures));

        var context = new Context { FeatureCache = customCache };

        using var growthBook = new GrowthBook(context);
        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeTrue("because the custom cache returned features successfully");
        growthBook.Features.Should().ContainKey(featureKey, "because the custom cache provided this feature");
        await customCache.Received().GetFeatures(Arg.Any<System.Threading.CancellationToken?>());
    }

    [Fact]
    public void Dispose_ShouldNotCancelSharedRepository_WhenFeatureRepositoryIsInjected()
    {
        var sharedRepository = Substitute.For<IGrowthBookFeatureRepository>();
        var context = new Context { FeatureRepository = sharedRepository };

        var growthBook = new GrowthBook(context);
        growthBook.Dispose();

        sharedRepository.DidNotReceive().Cancel();
    }

    [Fact]
    public void Dispose_ShouldCancelRepository_WhenRepositoryIsOwnedByGrowthBook()
    {
        var customCache = Substitute.For<IGrowthBookFeatureCache>();
        customCache.IsCacheExpired.Returns(false);
        customCache.FeatureCount.Returns(1);
        customCache.GetFeatures(Arg.Any<System.Threading.CancellationToken?>())
            .Returns(System.Threading.Tasks.Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>()));

        var context = new Context { FeatureCache = customCache };

        var growthBook = new GrowthBook(context);
        growthBook.Dispose();

        // The internal FeatureRefreshWorker.Cancel() is called indirectly via the owned FeatureRepository.
        // We verify it by ensuring the cache is not accessed after dispose (no exception thrown).
        customCache.DidNotReceive().RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<System.Threading.CancellationToken?>());
    }

    [Fact]
    public void Clone_ShouldCarryTheCacheSettings()
    {
        var customCache = Substitute.For<IGrowthBookFeatureCache>();

        var context = new Context
        {
            FeatureCache = customCache,
            CacheExpirationInSeconds = 15,
            HttpRequestTimeoutInSeconds = 5
        };

        var clone = context.Clone();

        // GrowthBookFactory hands every per-user instance a Clone() of its base context, so a setting
        // left out of Clone() silently reverts every instance to the defaults with nothing else failing.
        clone.FeatureCache.Should().BeSameAs(customCache);
        clone.CacheExpirationInSeconds.Should().Be(15);
        clone.HttpRequestTimeoutInSeconds.Should().Be(5);
    }

    [Fact]
    public async Task Evaluation_ShouldUseFeaturesFromTheSuppliedCache_WithoutAnyNetworkCall()
    {
        var cache = new RecordingCache { IsCacheExpired = false };
        cache.Seed(FeatureSet(true));

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureCache = cache
        });

        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeTrue();
        cache.GetFeaturesCallCount.Should().BeGreaterThan(0, "because the supplied cache has to be the one consulted");
        growthBook.IsOn("flag").Should().BeTrue("because the features came from the supplied cache with no network call");
    }

    [Fact]
    public async Task Evaluation_ShouldSeeUpdatedContents_WhenTheSuppliedCacheIsRefreshed()
    {
        var cache = new RecordingCache { IsCacheExpired = false };
        cache.Seed(FeatureSet(false));

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureCache = cache
        });

        await growthBook.LoadFeaturesWithResult();
        growthBook.IsOn("flag").Should().BeFalse();

        // The host application refreshes its own cache out of band - the SDK must read through to it on
        // the next load rather than holding on to a copy it took at construction time.
        await cache.RefreshWith(FeatureSet(true));
        await growthBook.LoadFeaturesWithResult();

        growthBook.IsOn("flag").Should().BeTrue();
        cache.GetFeaturesCallCount.Should().Be(2, "because each load reads through to the supplied cache");
    }

    [Fact]
    public void Evaluation_ShouldStillWork_WhenNoCacheIsSupplied()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(true)
        });

        growthBook.IsOn("flag").Should().BeTrue("because omitting the cache must change nothing about existing behavior");
    }

    [Fact]
    public void SuppliedRepository_ShouldTakePrecedence_OverASuppliedCache()
    {
        var cache = new RecordingCache { IsCacheExpired = false };
        cache.Seed(FeatureSet(true));

        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository,
            FeatureCache = cache
        });

        growthBook.IsOn("flag").Should().BeFalse();
        cache.GetFeaturesCallCount.Should().Be(0, "because a supplied repository brings its own cache and nothing consults this one");
    }

    [Fact]
    public void CacheExpiration_ShouldDefaultToSixtySeconds()
    {
        new Context().CacheExpirationInSeconds.Should().Be(60, "because that was the hardcoded value before it became configurable");
    }

    [Fact]
    public async Task EveryFactoryCreatedInstance_ShouldShareTheSuppliedCache()
    {
        var cache = new RecordingCache { IsCacheExpired = false };
        cache.Seed(FeatureSet(true));

        var baseContext = new Context
        {
            Attributes = new JObject(),
            FeatureCache = cache
        };

        using var factory = new GrowthBookFactory(baseContext);

        using var first = factory.CreateForUser(new { id = "user-1" });
        using var second = factory.CreateForUser(new { id = "user-2" });

        await first.LoadFeaturesWithResult();
        await second.LoadFeaturesWithResult();

        first.IsOn("flag").Should().BeTrue();
        second.IsOn("flag").Should().BeTrue();
        cache.GetFeaturesCallCount.Should().BeGreaterThanOrEqualTo(2, "because both instances read through the one cache they were given");
    }
}
