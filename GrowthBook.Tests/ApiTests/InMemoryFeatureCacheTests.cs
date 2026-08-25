using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class InMemoryFeatureCacheTests : UnitTest
{
    private const string FirstFeatureId = nameof(FirstFeatureId);
    private const string SecondFeatureId = nameof(SecondFeatureId);

    private readonly InMemoryFeatureCache _cache;
    private readonly Feature _firstFeature;
    private readonly Feature _secondFeature;
    private readonly Dictionary<string, Feature> _availableFeatures;

    public InMemoryFeatureCacheTests()
    {
        _cache = new(60);

        _firstFeature = new() { DefaultValue = 1 };
        _secondFeature = new() { DefaultValue = 2 };
        _availableFeatures = new()
        {
            [FirstFeatureId] = _firstFeature,
            [SecondFeatureId] = _secondFeature
        };
    }

    [Fact]
    public void CacheIsImmediatelyExpiredUponCreation()
    {
        _cache.IsCacheExpired.Should().BeTrue("because no attempt to refresh the cache with features has been made");
    }

    [Fact]
    public async Task FeatureCountAccuratelyReflectsCachedFeatures()
    {
        _cache.FeatureCount.Should().Be(0, "because no features have been cached yet");

        await _cache.RefreshWith(_availableFeatures);

        _cache.FeatureCount.Should().Be(_availableFeatures.Count, "because that's the number of features that were cached");
    }

    [Fact]
    public async Task CacheExpirationStatusWillChangeWhenCacheIsRefreshed()
    {
        _cache.IsCacheExpired.Should().BeTrue("because no attempt to refresh the cache with features has been made");

        await _cache.RefreshWith(_availableFeatures);

        _cache.IsCacheExpired.Should().BeFalse("because the expiration date shifts forward when the cache is refreshed");
    }

    [Fact]
    public async Task GetFeaturesWillRetrieveCopyOfCache()
    {
        await _cache.RefreshWith(_availableFeatures);

        var features = await _cache.GetFeatures();

        features.Should().NotBeNullOrEmpty("because at least one feature has been cached");
        features.Should().NotBeSameAs(_availableFeatures, "because a copy of the cache will be returned to discourage external cache manipulation");
        features.Should().BeEquivalentTo(_availableFeatures, "because all cached features will be present");
    }

    /// <summary>
    /// A clock the test moves by hand, so expiry can be exercised without waiting out the TTL in real
    /// seconds. Time only advances when a test says so, which is what makes these assertions deterministic
    /// under any amount of CPU contention.
    /// </summary>
    private sealed class ManualClock
    {
        private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    [Fact]
    public async Task ARefreshedCacheGoesStaleOnceItsTimeToLiveElapses()
    {
        var clock = new ManualClock();
        var cache = new InMemoryFeatureCache(60, clock.UtcNow);

        await cache.RefreshWith(_availableFeatures);

        cache.IsCacheExpired.Should().BeFalse("because the cache was just refreshed");

        clock.Advance(TimeSpan.FromSeconds(59));
        cache.IsCacheExpired.Should().BeFalse("because one second of the time to live is still left");

        clock.Advance(TimeSpan.FromSeconds(1));
        cache.IsCacheExpired.Should().BeTrue("because the expiration is inclusive - reaching it counts as expired");

        clock.Advance(TimeSpan.FromHours(1));
        cache.IsCacheExpired.Should().BeTrue("because nothing un-expires a cache except another refresh");
    }

    [Fact]
    public async Task RefreshingAStaleCacheExtendsTheTimeToLiveFromTheMomentOfTheRefresh()
    {
        var clock = new ManualClock();
        var cache = new InMemoryFeatureCache(60, clock.UtcNow);

        await cache.RefreshWith(_availableFeatures);

        clock.Advance(TimeSpan.FromSeconds(90));
        cache.IsCacheExpired.Should().BeTrue();

        await cache.RefreshWith(_availableFeatures);

        cache.IsCacheExpired.Should().BeFalse("because the window restarts from the refresh, not from the original expiry");

        clock.Advance(TimeSpan.FromSeconds(59));
        cache.IsCacheExpired.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1));
        cache.IsCacheExpired.Should().BeTrue();
    }

    [Fact]
    public async Task StaleFeaturesAreStillServedWhileTheCacheIsExpired()
    {
        var clock = new ManualClock();
        var cache = new InMemoryFeatureCache(60, clock.UtcNow);

        await cache.RefreshWith(_availableFeatures);

        clock.Advance(TimeSpan.FromSeconds(120));

        cache.IsCacheExpired.Should().BeTrue();
        cache.FeatureCount.Should().Be(_availableFeatures.Count, "because expiry marks the cache for refresh - it does not empty it");

        var features = await cache.GetFeatures();

        features.Should().BeEquivalentTo(_availableFeatures,
            "because serving stale values while a refresh is pending is the whole point of the expiry flag");
    }

    [Fact]
    public void ACacheWithNoTimeToLiveIsAlwaysExpired()
    {
        var clock = new ManualClock();
        var cache = new InMemoryFeatureCache(0, clock.UtcNow);

        cache.IsCacheExpired.Should().BeTrue("because it starts out pre-expired");

        cache.RefreshWith(_availableFeatures).GetAwaiter().GetResult();

        cache.IsCacheExpired.Should().BeTrue("because a zero second window expires the instant it is set");
    }
}
