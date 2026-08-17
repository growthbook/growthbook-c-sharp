using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.SSE;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class StreamingHostTests : ApiUnitTest<FeatureRefreshWorker>
{
    private static string GetPrivateField(FeatureRefreshWorker worker, string fieldName)
    {
        var field = typeof(FeatureRefreshWorker).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)field.GetValue(worker);
    }

    [Fact]
    public void SseEndpointUsesStreamingHostWhenConfigured()
    {
        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://cdn.growthbook.io",
            StreamingHost = "https://streaming.growthbook.io",
            ClientKey = "sdk-test"
        };
        var httpClientFactory = new HttpClientFactory();
        var worker = new FeatureRefreshWorker(_logger, httpClientFactory, config, _cache);

        var sseEndpoint = GetPrivateField(worker, "_serverSentEventsApiEndpoint");
        var featuresEndpoint = GetPrivateField(worker, "_featuresApiEndpoint");

        sseEndpoint.Should().Be("https://streaming.growthbook.io/sub/sdk-test");
        featuresEndpoint.Should().Be("https://cdn.growthbook.io/api/features/sdk-test", "because ApiHost is still used for the regular Features endpoint");
    }

    [Fact]
    public void SseFallsBackToApiHostWhenStreamingHostIsNotConfigured()
    {
        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://cdn.growthbook.io",
            ClientKey = "sdk-test"
        };
        var httpClientFactory = new HttpClientFactory();
        var worker = new FeatureRefreshWorker(_logger, httpClientFactory, config, _cache);

        var sseEndpoint = GetPrivateField(worker, "_serverSentEventsApiEndpoint");

        sseEndpoint.Should().Be("https://cdn.growthbook.io/sub/sdk-test");
    }

    [Fact]
    public void SseEndpointTrimsStreamingHostTrailingSlashes()
    {
        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://cdn.growthbook.io",
            StreamingHost = "https://streaming.growthbook.io///",
            ClientKey = "sdk-test"
        };
        var httpClientFactory = new HttpClientFactory();
        var worker = new FeatureRefreshWorker(_logger, httpClientFactory, config, _cache);

        var sseEndpoint = GetPrivateField(worker, "_serverSentEventsApiEndpoint");

        sseEndpoint.Should().Be("https://streaming.growthbook.io/sub/sdk-test");
    }

    [Fact]
    public void SseClientStoresProvidedHeaders()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test-token" };
        var httpClientFactory = new HttpClientFactory();
        var sseLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SSEClient>.Instance;

        var sseClient = new SSEClient(sseLogger, httpClientFactory, "https://streaming.growthbook.io/sub/sdk-test", headers);

        var storedHeaders = (Dictionary<string, string>)typeof(SSEClient)
            .GetField("_headers", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(sseClient);

        storedHeaders.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer test-token");
    }

    [Fact]
    public void ContextCloneCreatesAnIndependentCopyOfTheNewProperties()
    {
        var context = new Context
        {
            StreamingHost = "https://streaming.growthbook.io",
            ApiHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer original" },
            StreamingHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer original" }
        };

        var cloned = context.Clone();
        cloned.StreamingHost = "https://changed.example.com";
        cloned.ApiHostRequestHeaders["Authorization"] = "Bearer changed";
        cloned.StreamingHostRequestHeaders["Authorization"] = "Bearer changed";

        context.StreamingHost.Should().Be("https://streaming.growthbook.io");
        context.ApiHostRequestHeaders["Authorization"].Should().Be("Bearer original");
        context.StreamingHostRequestHeaders["Authorization"].Should().Be("Bearer original");
    }
}
