using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class LruETagCacheTests : UnitTest
{
    private readonly LruETagCache _cache;

    public LruETagCacheTests()
    {
        _cache = new LruETagCache(3);
    }

    [Fact]
    public void BasicPutAndGet()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");

        _cache.Get("url1").Should().Be("etag1");
        _cache.Get("url2").Should().Be("etag2");
        _cache.Get("url3").Should().BeNull();
    }

    [Fact]
    public void LruEvictionWhenCapacityExceeded()
    {
        // Fill cache to capacity
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");
        _cache.Put("url3", "etag3");

        // Add one more, should evict url1 (least recently used)
        _cache.Put("url4", "etag4");

        _cache.Get("url1").Should().BeNull("because url1 was evicted");
        _cache.Get("url2").Should().Be("etag2");
        _cache.Get("url3").Should().Be("etag3");
        _cache.Get("url4").Should().Be("etag4");
        _cache.Size().Should().Be(3);
    }

    [Fact]
    public void AccessingEntryUpdatesItsPositionInLru()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");
        _cache.Put("url3", "etag3");

        // Access url1 to make it recently used
        _cache.Get("url1");

        // Add url4, should evict url2 (now the least recently used)
        _cache.Put("url4", "etag4");

        _cache.Get("url1").Should().Be("etag1", "because url1 is still present");
        _cache.Get("url2").Should().BeNull("because url2 was evicted");
        _cache.Get("url3").Should().Be("etag3");
        _cache.Get("url4").Should().Be("etag4");
    }

    [Fact]
    public void PutNullRemovesEntry()
    {
        _cache.Put("url1", "etag1");
        _cache.Get("url1").Should().Be("etag1");

        _cache.Put("url1", null);
        _cache.Get("url1").Should().BeNull();
        _cache.Size().Should().Be(0);
    }

    [Fact]
    public void RemoveOperation()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");

        var removed = _cache.Remove("url1");

        removed.Should().Be("etag1");
        _cache.Get("url1").Should().BeNull();
        _cache.Size().Should().Be(1);
    }

    [Fact]
    public void RemoveNonExistentEntry()
    {
        var removed = _cache.Remove("nonexistent");

        removed.Should().BeNull();
    }

    [Fact]
    public void ClearOperation()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");
        _cache.Put("url3", "etag3");

        _cache.Clear();

        _cache.Size().Should().Be(0);
        _cache.Get("url1").Should().BeNull();
        _cache.Get("url2").Should().BeNull();
        _cache.Get("url3").Should().BeNull();
    }

    [Fact]
    public void ContainsOperation()
    {
        _cache.Put("url1", "etag1");

        _cache.Contains("url1").Should().BeTrue();
        _cache.Contains("url2").Should().BeFalse();
    }

    [Fact]
    public void SizeOperation()
    {
        _cache.Size().Should().Be(0);

        _cache.Put("url1", "etag1");
        _cache.Size().Should().Be(1);

        _cache.Put("url2", "etag2");
        _cache.Size().Should().Be(2);

        _cache.Put("url3", "etag3");
        _cache.Size().Should().Be(3);

        // Adding a 4th entry should keep size at 3 (evicts one)
        _cache.Put("url4", "etag4");
        _cache.Size().Should().Be(3);
    }

    [Fact]
    public void UpdatingExistingEntryDoesNotGrowCache()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");
        _cache.Size().Should().Be(2);

        // Update existing entry
        _cache.Put("url1", "etag1-updated");

        // Size should still be 2
        _cache.Size().Should().Be(2);
        _cache.Get("url1").Should().Be("etag1-updated");
    }

    [Fact]
    public void LargeCacheOperations()
    {
        var largeCache = new LruETagCache(100);

        // Add 150 items
        for (int i = 0; i < 150; i++)
        {
            largeCache.Put($"url{i}", $"etag{i}");
        }

        // Should only have 100 items (the most recent ones)
        largeCache.Size().Should().Be(100);

        // First 50 should be evicted
        for (int i = 0; i < 50; i++)
        {
            largeCache.Get($"url{i}").Should().BeNull($"because url{i} was evicted");
        }

        // Last 100 should be present
        for (int i = 50; i < 150; i++)
        {
            largeCache.Get($"url{i}").Should().Be($"etag{i}");
        }
    }

    [Fact]
    public void DefaultMaxSize()
    {
        var defaultCache = new LruETagCache();

        // Add 101 items
        for (int i = 0; i < 101; i++)
        {
            defaultCache.Put($"url{i}", $"etag{i}");
        }

        // Should only have 100 items (default max size)
        defaultCache.Size().Should().Be(100);

        // First entry should be evicted
        defaultCache.Get("url0").Should().BeNull();

        // Last 100 should be present
        for (int i = 1; i <= 100; i++)
        {
            defaultCache.Get($"url{i}").Should().Be($"etag{i}");
        }
    }

    [Fact]
    public void MinMaxSize()
    {
        // Even with 0 or negative max size, should have at least 1
        var tinyCache = new LruETagCache(0);
        tinyCache.Put("url1", "etag1");
        tinyCache.Size().Should().Be(1);

        tinyCache.Put("url2", "etag2");
        tinyCache.Size().Should().Be(1);
        tinyCache.Get("url1").Should().BeNull("because url1 was evicted");
        tinyCache.Get("url2").Should().Be("etag2");
    }

    [Fact]
    public void GetDoesNotAffectNonExistentKey()
    {
        _cache.Put("url1", "etag1");

        // Get non-existent key should return null without side effects
        var result = _cache.Get("nonexistent");

        result.Should().BeNull();
        _cache.Size().Should().Be(1);
    }

    [Fact]
    public void MultipleUpdatesToSameKey()
    {
        _cache.Put("url1", "etag1");
        _cache.Put("url1", "etag2");
        _cache.Put("url1", "etag3");

        _cache.Size().Should().Be(1);
        _cache.Get("url1").Should().Be("etag3");
    }

    [Fact]
    public void EvictionOrderWithMixedOperations()
    {
        // Add entries
        _cache.Put("url1", "etag1");
        _cache.Put("url2", "etag2");
        _cache.Put("url3", "etag3");

        // Access url1 and url2, update url3
        _cache.Get("url1");
        _cache.Get("url2");
        _cache.Put("url3", "etag3-updated");

        // Add url4 - should evict url1 since it was accessed first
        _cache.Put("url4", "etag4");

        _cache.Get("url1").Should().BeNull("because url1 was evicted");
        _cache.Get("url2").Should().NotBeNull();
        _cache.Get("url3").Should().NotBeNull();
        _cache.Get("url4").Should().NotBeNull();
    }

    [Fact]
    public void GetWithNullUrl()
    {
        _cache.Put("url1", "etag1");

        var result = _cache.Get(null);

        result.Should().BeNull();
        _cache.Size().Should().Be(1);
    }

    [Fact]
    public void PutWithNullUrl()
    {
        _cache.Put(null, "etag1");

        _cache.Size().Should().Be(0, "because null URL should be ignored");
    }

    [Fact]
    public void ContainsWithNullUrl()
    {
        _cache.Put("url1", "etag1");

        _cache.Contains(null).Should().BeFalse();
    }

    [Fact]
    public void RemoveWithNullUrl()
    {
        _cache.Put("url1", "etag1");

        var result = _cache.Remove(null);

        result.Should().BeNull();
        _cache.Size().Should().Be(1);
    }
}
