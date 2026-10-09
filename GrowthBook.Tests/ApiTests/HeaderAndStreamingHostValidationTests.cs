using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

// NOTE: TS itself does zero validation here (see feature-repository.ts / core.ts getApiHosts) -
// this strictness is a deliberate C#-specific addition, not TS parity.
public class HeaderAndStreamingHostValidationTests
{
    [Theory]
    [InlineData("User-Agent")]
    [InlineData("If-None-Match")]
    [InlineData("Cache-Control")]
    [InlineData("user-agent")]
    [InlineData("CACHE-CONTROL")]
    public void ReservedHeaderInApiHostRequestHeadersThrowsAtConstruction(string reservedHeaderName)
    {
        var context = new Context
        {
            ApiHostRequestHeaders = new Dictionary<string, string> { [reservedHeaderName] = "some-value" }
        };

        Assert.Throws<ArgumentException>(() => new GrowthBook(context));
    }

    [Theory]
    [InlineData("User-Agent")]
    [InlineData("If-None-Match")]
    [InlineData("Cache-Control")]
    public void ReservedHeaderInStreamingHostRequestHeadersThrowsAtConstruction(string reservedHeaderName)
    {
        var context = new Context
        {
            StreamingHostRequestHeaders = new Dictionary<string, string> { [reservedHeaderName] = "some-value" }
        };

        Assert.Throws<ArgumentException>(() => new GrowthBook(context));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    public void InvalidStreamingHostUrlThrowsAtConstruction(string invalidStreamingHost)
    {
        var context = new Context { StreamingHost = invalidStreamingHost };

        Assert.Throws<ArgumentException>(() => new GrowthBook(context));
    }

    [Fact]
    public void ValidHttpsStreamingHostDoesNotThrow()
    {
        var context = new Context { StreamingHost = "https://streaming.growthbook.io" };

        var growthBook = new GrowthBook(context);
        growthBook.Should().NotBeNull();
    }

    [Fact]
    public void NonReservedApiHostRequestHeadersDoNotThrow()
    {
        var context = new Context
        {
            ApiHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer token" }
        };

        var growthBook = new GrowthBook(context);
        growthBook.Should().NotBeNull();
    }
}
