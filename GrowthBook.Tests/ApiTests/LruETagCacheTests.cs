using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class LruETagCacheTests
{
    [Fact]
    public void PutAndGetStoresETagByUrl()
    {
        var cache = new LruETagCache(2);

        cache.Put("url1", "\"etag1\"");

        cache.Get("url1").Should().Be("\"etag1\"");
        cache.Get("missing").Should().BeNull();
    }

    [Fact]
    public void LeastRecentlyUsedEntryIsEvictedWhenCapacityIsExceeded()
    {
        var cache = new LruETagCache(2);

        cache.Put("url1", "\"etag1\"");
        cache.Put("url2", "\"etag2\"");
        cache.Get("url1");
        cache.Put("url3", "\"etag3\"");

        cache.Get("url1").Should().Be("\"etag1\"");
        cache.Get("url2").Should().BeNull();
        cache.Get("url3").Should().Be("\"etag3\"");
        cache.Size().Should().Be(2);
    }

    [Fact]
    public void PutNullRemovesExistingETag()
    {
        var cache = new LruETagCache();

        cache.Put("url1", "\"etag1\"");
        cache.Put("url1", null);

        cache.Get("url1").Should().BeNull();
        cache.Size().Should().Be(0);
    }
}
