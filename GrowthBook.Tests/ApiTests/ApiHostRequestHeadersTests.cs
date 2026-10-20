using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class ApiHostRequestHeadersTests
{
    private sealed class FeaturesResponse
    {
        public Dictionary<string, Feature> Features { get; set; }
    }

    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage LastRequest { get; private set; }

        public RequestCapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _respond(request);
        }
    }

    [Fact]
    public async Task ApiHostRequestHeadersAreIncludedInFeaturesGetRequest()
    {
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } } });
        var handler = new RequestCapturingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var httpClient = new HttpClient(handler);
        var logger = Substitute.For<ILogger>();

        var config = new GrowthBookConfigurationOptions
        {
            ApiHostRequestHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer test-token",
                ["X-Internal-Gateway-Key"] = "abc123"
            }
        };

        await httpClient.GetFeaturesFrom("https://cdn.growthbook.io/api/features/sdk-test", logger, config, CancellationToken.None);

        handler.LastRequest.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-token");
        handler.LastRequest.Headers.GetValues("X-Internal-Gateway-Key").Should().ContainSingle().Which.Should().Be("abc123");
    }

    [Fact]
    public async Task ApiHostRequestHeadersAreIncludedInRemoteEvaluationPostRequest()
    {
        var apiResponse = new RemoteEvaluationResponse
        {
            Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } },
            DateUpdated = DateTimeOffset.UtcNow
        };
        var json = JsonConvert.SerializeObject(apiResponse);
        var handler = new RequestCapturingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Is(ConfiguredClients.DefaultApiClient)).Returns(httpClient);

        var service = new RemoteEvaluationService(Substitute.For<ILogger<RemoteEvaluationService>>(), httpClientFactory);
        var request = new RemoteEvaluationRequest { Attributes = new JObject { ["id"] = "user_123" } };

        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test-token" };

        var result = await service.EvaluateAsync("https://api.example.com", "clientKey", request, headers);

        result.IsSuccess.Should().BeTrue();
        handler.LastRequest.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task ApiHostRequestHeadersReachTheRemoteEvaluationPostThroughTheRealLoadFeaturesPath()
    {
        // The other remote-eval test above calls EvaluateAsync directly. This one goes through the
        // real path a consumer hits - GrowthBook.LoadFeatures -> CreateCurrentContext ->
        // FeatureRepository.GetFeaturesWithContext -> RemoteEvaluationService - which is where the
        // headers can get silently dropped if the per-request context doesn't carry them.
        var apiResponse = new RemoteEvaluationResponse
        {
            Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } },
            DateUpdated = DateTimeOffset.UtcNow
        };
        var json = JsonConvert.SerializeObject(apiResponse);
        var handler = new RequestCapturingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var remoteEvaluationService = new RemoteEvaluationService(Substitute.For<ILogger<RemoteEvaluationService>>(), httpClientFactory);
        var repository = new FeatureRepository(
            Substitute.For<ILogger<FeatureRepository>>(),
            Substitute.For<IGrowthBookFeatureCache>(),
            Substitute.For<IGrowthBookFeatureRefreshWorker>(),
            remoteEvaluationService);

        var context = new Context
        {
            RemoteEval = true,
            ApiHost = "https://api.example.com",
            ClientKey = "clientKey",
            ApiHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer remote-eval-token" },
            FeatureRepository = repository
        };

        using var growthBook = new GrowthBook(context);
        await growthBook.LoadFeatures();

        handler.LastRequest.Should().NotBeNull("because a remote evaluation POST should have been made");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer remote-eval-token");
    }

    [Fact]
    public async Task ExistingETagBehaviorIsUnchangedWhenApiHostRequestHeadersAreConfigured()
    {
        var etag = "test-etag";
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } } });

        var handler = new RequestCapturingHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{etag}\"");
            return Task.FromResult(response);
        });
        var httpClient = new HttpClient(handler);
        var logger = Substitute.For<ILogger>();
        var etagCache = new LruETagCache();

        var config = new GrowthBookConfigurationOptions
        {
            ApiHostRequestHeaders = new Dictionary<string, string> { ["X-Custom"] = "value" }
        };

        var endpoint = "https://cdn.growthbook.io/api/features/sdk-test";

        await httpClient.GetFeaturesFrom(endpoint, logger, config, CancellationToken.None, etagCache);
        handler.LastRequest.Headers.GetValues("X-Custom").Should().ContainSingle().Which.Should().Be("value");
        handler.LastRequest.Headers.IfNoneMatch.Should().BeEmpty("because the first request has no cached ETag yet");

        await httpClient.GetFeaturesFrom(endpoint, logger, config, CancellationToken.None, etagCache);
        handler.LastRequest.Headers.GetValues("X-Custom").Should().ContainSingle().Which.Should().Be("value");
        handler.LastRequest.Headers.IfNoneMatch.Should().ContainSingle().Which.Tag.Trim('"').Should().Be(etag, "because ApiHostRequestHeaders must not interfere with the SDK's own conditional-request header");
    }
}
