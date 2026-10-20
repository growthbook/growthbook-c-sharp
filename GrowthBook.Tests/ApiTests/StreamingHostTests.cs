using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Records every request the SDK makes, so a test can see the URL and the headers that actually went
    /// out rather than a field on an object it constructed itself.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _featuresJson;

        public RecordingHandler(string featuresJson) => _featuresJson = featuresJson;

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public ManualResetEventSlim StreamRequested { get; } = new ManualResetEventSlim(false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            if (request.RequestUri.AbsolutePath.StartsWith("/sub/"))
            {
                StreamRequested.Set();

                // Held open, the way a stream is, so the worker does not reconnect while the test looks.
                var pending = new TaskCompletionSource<HttpResponseMessage>();

                cancellationToken.Register(() => pending.TrySetCanceled());

                return pending.Task;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_featuresJson, Encoding.UTF8, "application/json")
            };

            response.Headers.TryAddWithoutValidation("x-sse-support", "enabled");

            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public RecordingHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure) =>
            configure(new HttpClient(_handler, disposeHandler: false));
    }

    /// <summary>
    /// Drives a real refresh through the worker and reads the request that reached the transport. The
    /// sibling test only inspects a field on an SSEClient the test built itself, so it would still hold
    /// if the worker stopped passing either the dedicated host or the headers on.
    /// </summary>
    [Fact]
    public async Task TheStreamingRequestCarriesTheDedicatedHostAndItsHeaders()
    {
        var handler = new RecordingHandler("{\"features\":{}}");

        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://api.example.test",
            ClientKey = "sdk-test",
            StreamingHost = "https://streaming.example.test",
            StreamingHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer streaming-token" },
            PreferServerSentEvents = true
        };

        using var worker = new FeatureRefreshWorker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRefreshWorker>.Instance,
            new RecordingHttpClientFactory(handler),
            config,
            new InMemoryFeatureCache(60));

        await worker.RefreshCacheFromApi();

        handler.StreamRequested.Wait(TimeSpan.FromSeconds(10))
            .Should().BeTrue("the worker has to open the stream once the API reports server-sent events are supported");

        worker.Cancel();

        HttpRequestMessage streamRequest;

        lock (handler.Requests)
        {
            streamRequest = handler.Requests.Last(r => r.RequestUri.AbsolutePath.StartsWith("/sub/"));
        }

        streamRequest.RequestUri.ToString().Should().Be("https://streaming.example.test/sub/sdk-test",
            "the stream goes to the dedicated host, not the API host");

        streamRequest.Headers.TryGetValues("Authorization", out var authorization).Should().BeTrue(
            "the streaming headers have to reach the request, not just the client that makes it");

        authorization.Should().ContainSingle().Which.Should().Be("Bearer streaming-token");
    }

    [Fact]
    public void AWorkerBuiltDirectlyRejectsAReservedHeader()
    {
        // The Context path checks this, but FeatureRefreshWorker is public and takes the options straight
        // from the caller, so it could send a caller's own If-None-Match before anything was cached.
        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://api.example.test",
            ClientKey = "sdk-test",
            ApiHostRequestHeaders = new Dictionary<string, string> { ["If-None-Match"] = "\"forced\"" }
        };

        var build = () => new FeatureRefreshWorker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRefreshWorker>.Instance,
            new HttpClientFactory(),
            config,
            new InMemoryFeatureCache(60));

        build.Should().Throw<ArgumentException>().WithMessage("*If-None-Match*reserved*");
    }

    [Fact]
    public void AWorkerBuiltDirectlyRejectsAnInvalidStreamingHost()
    {
        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://api.example.test",
            ClientKey = "sdk-test",
            StreamingHost = "not-a-url"
        };

        var build = () => new FeatureRefreshWorker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRefreshWorker>.Instance,
            new HttpClientFactory(),
            config,
            new InMemoryFeatureCache(60));

        build.Should().Throw<ArgumentException>().WithMessage("*not-a-url*");
    }
}
