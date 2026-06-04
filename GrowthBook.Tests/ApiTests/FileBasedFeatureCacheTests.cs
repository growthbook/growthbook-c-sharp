using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class FileBasedFeatureCacheTests : IDisposable
{
    private const string FirstFeatureId = nameof(FirstFeatureId);
    private const string SecondFeatureId = nameof(SecondFeatureId);

    private readonly string _testCachePath;
    private readonly FileBasedFeatureCache _cache;
    private readonly Feature _firstFeature;
    private readonly Feature _secondFeature;
    private readonly Dictionary<string, Feature> _availableFeatures;

    public FileBasedFeatureCacheTests()
    {
        _testCachePath = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}");

        _cache = new FileBasedFeatureCache(cacheExpirationInSeconds: 60);
        _cache.SetCustomCachePath(_testCachePath);

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

    [Fact]
    public async Task FeaturesArePersistedToDiskAfterRefresh()
    {
        await _cache.RefreshWith(_availableFeatures);

        var secondCache = new FileBasedFeatureCache(cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        var features = await secondCache.GetFeatures();

        features.Should().NotBeEmpty("because features were persisted to disk by the first cache instance");
        features.Should().ContainKey(FirstFeatureId);
        features.Should().ContainKey(SecondFeatureId);
    }

    [Fact]
    public async Task ClearCacheRemovesBothMemoryAndFileData()
    {
        await _cache.RefreshWith(_availableFeatures);

        _cache.ClearCache();

        _cache.FeatureCount.Should().Be(0, "because clearing the cache empties the in-memory features");
        _cache.IsCacheExpired.Should().BeTrue("because clearing the cache resets the expiration");

        var secondCache = new FileBasedFeatureCache(cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        var features = await secondCache.GetFeatures();
        features.Should().BeEmpty("because clearing the cache deletes the file from disk");
    }

    [Fact]
    public async Task SetCacheKeyIsolatesCacheByDirectory()
    {
        _cache.SetCacheKey("key-a");
        await _cache.RefreshWith(_availableFeatures);

        var otherCache = new FileBasedFeatureCache(cacheExpirationInSeconds: 60);
        otherCache.SetCustomCachePath(_testCachePath);
        otherCache.SetCacheKey("key-b");

        var features = await otherCache.GetFeatures();
        features.Should().BeEmpty("because a different cache key maps to a different directory");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCachePath))
            {
                Directory.Delete(_testCachePath, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
