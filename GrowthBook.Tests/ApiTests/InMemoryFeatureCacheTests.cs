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
}
