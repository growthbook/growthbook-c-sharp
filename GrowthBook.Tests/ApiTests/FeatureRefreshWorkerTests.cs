using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class FeatureRefreshWorkerTests : ApiUnitTest<FeatureRefreshWorker>
{
    public class TestDelegatingHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestDelegatingHandler(HttpStatusCode statusCode, string jsonContent, string streamJsonContent, bool isServerSideEventsEnabled)
        {
            // This infrastructure is built for the purpose of making the background listener
            // integration test work properly, so it has several pieces of logic that will be called out here
            // for future reference.

            // We're keeping track of the handle count to determine whether to return a string or a stream.
            // If we need to do more of these tests in the future, this should be refactored and cleaned up.

            var handleCount = 0;

            _handler = (request, cancellationToken) =>
            {
                // Don't allow more than a single string and single stream content because the
                // integration test is geared towards a finite amount of responses being recorded.

                if (handleCount >= 2)
                {
                    return null;
                }

                HttpContent content = (isServerSideEventsEnabled && handleCount >= 1) switch
                {
                    true => new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes($"data: {streamJsonContent}"))),
                    false => new StringContent(jsonContent)
                };

                var response = new HttpResponseMessage(statusCode) { Content = content };

                if (isServerSideEventsEnabled)
                {
                    // Indicate in the HTTP response that the server sent events are supported
                    // in order to allow kicking off the background listener.

                    response.Headers.Add(HttpHeaders.ServerSentEvents.Key, HttpHeaders.ServerSentEvents.EnabledValue);
                }

                handleCount++;

                return Task.FromResult(response);
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private class TestHttpClientFactory : HttpClientFactory
    {
        private TestDelegatingHandler _handler;
        public bool IsServerSentEventsEnabled { get; set; }
        public Dictionary<string, Feature> ResponseContent { get; set; }
        public Dictionary<string, Feature> StreamResponseContent { get; set; }
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure)
        {
            // We're sending both of the string and stream contents because the handler will serve up both of them.
            // Also, we're reusing the handler here so we can accurately keep track of the shared call amounts
            // between the two paths.

            var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = ResponseContent });
            var streamJson = JsonConvert.SerializeObject(new FeaturesResponse { Features = StreamResponseContent });
            var httpClient = new HttpClient(_handler ??= new TestDelegatingHandler(ResponseStatusCode, json, streamJson, IsServerSentEventsEnabled));

            return configure(httpClient);
        }
    }

    private sealed class FeaturesResponse
    {
        public int FeatureCount { get; set; }
        public Dictionary<string, Feature> Features { get; set; }
        public string EncryptedFeatures { get; set; }
    }

    private readonly TestHttpClientFactory _httpClientFactory;
    private readonly GrowthBookConfigurationOptions _config;
    private readonly FeatureRefreshWorker _worker;

    public FeatureRefreshWorkerTests()
    {
        _config = new();
        _httpClientFactory = new TestHttpClientFactory();
        _httpClientFactory.ResponseContent = _availableFeatures;
        _httpClientFactory.StreamResponseContent = _availableFeatures.Take(1).ToDictionary(x => x.Key, x => x.Value);
        _worker = new(_logger, _httpClientFactory, _config, _cache);
    }


    [Fact]
    public async Task HttpRequestWithSuccessStatusThatPrefersApiCallWillGetFeaturesFromApiAndRefreshCache()
    {
        _config.PreferServerSentEvents = false;

        _cache
            .RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>())
            .Returns(Task.CompletedTask);

        var features = await _worker.RefreshCacheFromApi();

        features.Should().BeEquivalentTo(_availableFeatures);

        await _cache.Received(1).RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>());
    }

    [Fact]
    public async Task HttpResponseWithServerSentEventSupportWillStartBackgroundListenerIfPreferred()
    {
        _config.PreferServerSentEvents = true;
        _httpClientFactory.IsServerSentEventsEnabled = true;

        // We need to collect the cache attempts for comparison and verification. We also need to
        // make sure that the test method doesn't get ahead of the refresh attempts so we're
        // adding in a reset event that will be triggered on every cache refresh to let the test
        // incrementally move forward when it's appropriate.

        var cachedResults = new ConcurrentQueue<IDictionary<string, Feature>>();
        var resetEvent = new ManualResetEventSlim(false);

        _cache
            .RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                cachedResults.Enqueue((IDictionary<string, Feature>)x[0]);

                if (cachedResults.Count > 1)
                {
                    resetEvent.Set();
                }
            });

        var features = await _worker.RefreshCacheFromApi();

        resetEvent.Wait(5000).Should().BeTrue("because the cache should be refreshed within 5 seconds");

        _worker.Cancel();

        cachedResults.Count.Should().Be(2, "because the initial API call refreshed the cache once and the server sent listener refreshed it a second time");
        cachedResults.TryDequeue(out var first);
        cachedResults.TryDequeue(out var second);
        first.Should().BeEquivalentTo(_httpClientFactory.ResponseContent, "because those are the features returned from the initial API call");
        second.Should().BeEquivalentTo(_httpClientFactory.StreamResponseContent, "because those are the features returned from the server sent events API call");
    }
}
