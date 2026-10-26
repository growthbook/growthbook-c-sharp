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
using GrowthBook.Api.Extensions;
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

    public class ETagTestDelegatingHandler : DelegatingHandler
    {
        private readonly string _etag;
        private readonly string _jsonContent;
        private readonly bool _returnNotModifiedOnSecondCall;
        private int _callCount;

        public ETagTestDelegatingHandler(string etag, string jsonContent, bool returnNotModifiedOnSecondCall = false)
        {
            _etag = etag;
            _jsonContent = jsonContent;
            _returnNotModifiedOnSecondCall = returnNotModifiedOnSecondCall;
        }

        public bool ReceivedIfNoneMatchHeader { get; private set; }
        public string ReceivedETagValue { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.IfNoneMatch.Any())
            {
                ReceivedIfNoneMatchHeader = true;
                ReceivedETagValue = request.Headers.IfNoneMatch.First().Tag?.Trim('"');
            }

            _callCount++;

            if (_returnNotModifiedOnSecondCall && _callCount > 1)
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{_etag}\"");
                return Task.FromResult(notModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_jsonContent)
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{_etag}\"");

            return Task.FromResult(response);
        }
    }

    private class ETagHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public ETagHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure)
        {
            return configure(new HttpClient(_handler, disposeHandler: false));
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

    [Fact(Skip = "SSE integration test needs update for new SSE client implementation")]
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

    [Fact]
    public async Task ETagIsStoredAndSentOnLaterFeatureRequests()
    {
        var etag = "test-etag";
        var endpoint = "https://cdn.growthbook.io/api/features/sdk-test";
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });
        var handler = new ETagTestDelegatingHandler(etag, json);
        var httpClient = new HttpClient(handler);
        var etagCache = new LruETagCache();

        var first = await httpClient.GetFeaturesFrom(endpoint, _logger, _config, CancellationToken.None, etagCache);
        first.Features.Should().BeEquivalentTo(_availableFeatures);
        handler.ReceivedIfNoneMatchHeader.Should().BeFalse("because the first request has no cached ETag yet");

        var second = await httpClient.GetFeaturesFrom(endpoint, _logger, _config, CancellationToken.None, etagCache);
        second.Features.Should().BeEquivalentTo(_availableFeatures);
        handler.ReceivedIfNoneMatchHeader.Should().BeTrue("because the ETag from the first response should be reused");
        handler.ReceivedETagValue.Should().Be(etag);
    }

    [Fact]
    public async Task PublicGetFeaturesFromOverloadDoesNotRequireETagCache()
    {
        var endpoint = "https://cdn.growthbook.io/api/features/sdk-test";
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });
        var handler = new ETagTestDelegatingHandler("public-overload-etag", json);
        var httpClient = new HttpClient(handler);

        var response = await httpClient.GetFeaturesFrom(endpoint, _logger, _config, CancellationToken.None);

        response.Features.Should().BeEquivalentTo(_availableFeatures);
        handler.ReceivedIfNoneMatchHeader.Should().BeFalse("because the public overload has no ETag cache");
    }


    /// <summary>
    /// A 304 says the cached features are still current, so the window has to restart. The worker used
    /// to ask for that by checking for one concrete cache type, which meant a caller's own cache - the
    /// whole point of Context.FeatureCache - stayed expired and sent the next load straight back to the
    /// API, every time, until the features actually changed.
    /// </summary>

    /// <summary>
    /// A 304 answers the stored ETag, not the cache. Once the cache has been emptied — ClearCache, or
    /// a file that went away — "nothing changed" is true of the server and false of us, and treating
    /// the empty cache as fresh would hide the features for good: every later request gets the same
    /// 304 and nothing ever refills it.
    /// </summary>
    [Fact]
    public async Task AClearedCacheIsRefilledEvenWhenTheServerReportsNoChange()
    {
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });

        // 304 whenever an If-None-Match arrives, 200 otherwise - which is what a real server does, and
        // what makes dropping the stored ETag the thing that recovers.
        var handler = new ConditionalETagHandler("cleared-cache-etag", json);
        var cachePath = Path.Combine(Path.GetTempPath(), $"gb-cleared-{Guid.NewGuid():N}");

        try
        {
            var cache = new FileBasedFeatureCache("cleared-client", cacheExpirationInSeconds: 60, cachePath: cachePath);
            var worker = new FeatureRefreshWorker(_logger, new ETagHttpClientFactory(handler), _config, cache);

            (await worker.RefreshCacheFromApi()).Should().NotBeNull();
            cache.FeatureCount.Should().Be(_availableFeatures.Count);

            cache.ClearCache();
            cache.FeatureCount.Should().Be(0);

            var refilled = await worker.RefreshCacheFromApi();

            refilled.Should().NotBeNull("a 304 against an empty cache has to be answered by asking for the whole payload");
            cache.FeatureCount.Should().Be(_availableFeatures.Count, "and the features have to be back");
            cache.IsCacheExpired.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
    }

    /// <summary>
    /// A payload with no features in it is a correct answer, and an empty cache holding it is correct
    /// too. Treating empty as lost made every later 304 throw the ETag away and ask again for the same
    /// empty answer, with the caller waiting for both requests.
    /// </summary>
    [Fact]
    public async Task AnUnchangedEmptyPayloadDoesNotProvokeASecondRequest()
    {
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = new Dictionary<string, Feature>() });

        var handler = new ConditionalETagHandler("empty-payload-etag", json);
        var cachePath = Path.Combine(Path.GetTempPath(), $"gb-empty-{Guid.NewGuid():N}");

        try
        {
            var cache = new FileBasedFeatureCache("empty-client", cacheExpirationInSeconds: 60, cachePath: cachePath);
            var worker = new FeatureRefreshWorker(_logger, new ETagHttpClientFactory(handler), _config, cache);

            (await worker.RefreshCacheFromApi()).Should().NotBeNull();

            cache.FeatureCount.Should().Be(0, "the payload really has no features in it");
            handler.Requests.Should().Be(1);

            var afterNotModified = await worker.RefreshCacheFromApi();

            handler.Requests.Should().Be(2,
                "one conditional request answered 304 - and no second request for the same empty answer");
            afterNotModified.Should().NotBeNull().And.BeEmpty();
            cache.IsCacheExpired.Should().BeFalse("the 304 restarted the window instead");
        }
        finally
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Two refreshes overlapping must not leave the recorded count describing a different payload
    /// than the one the cache holds. One can save an empty map and be suspended before recording its
    /// count while another saves a full one and records two; when the first resumes and writes zero,
    /// a later ClearCache looks like an ordinary empty payload and the flags never come back.
    /// </summary>
    [Fact]
    public async Task OverlappingRefreshesDoNotLeaveTheCountDescribingAnotherPayload()
    {
        var empty = JsonConvert.SerializeObject(new FeaturesResponse { Features = new Dictionary<string, Feature>() });
        var full = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });

        var handler = new SequencedETagHandler(empty, full);
        var cache = new GatedCache();
        var worker = new FeatureRefreshWorker(_logger, new ETagHttpClientFactory(handler), _config, cache);

        // The first refresh saves the empty payload and is held inside RefreshWith, which is exactly
        // the window between saving the features and recording how many there were.
        var first = Task.Run(() => worker.RefreshCacheFromApi());

        cache.FirstWriteStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var second = Task.Run(() => worker.RefreshCacheFromApi());

        // Give the second refresh the chance to get all the way through, which is what creates the
        // bad pairing: it records two while the first is still suspended, and the first then records
        // zero on top. Keeping the store and the count together is what stops it finishing here.
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1)));

        cache.ReleaseFirstWrite();

        await Task.WhenAll(first, second);

        // Whatever order they finished in, the cache and the recorded count have to agree. Clearing
        // the cache now must therefore read as lost features rather than as an empty payload.
        cache.Clear();

        var recovered = await worker.RefreshCacheFromApi();

        recovered.Should().NotBeNullOrEmpty(
            "a cleared cache has to be refilled, and that only happens when the recorded count " +
            "describes the payload the cache was actually holding");
    }

    /// <summary>
    /// Serves the given payloads in order, then answers 304 to a conditional request and repeats the
    /// last payload to an unconditional one.
    /// </summary>
    private sealed class SequencedETagHandler : DelegatingHandler
    {
        private readonly string[] _payloads;
        private int _served;

        public int Served => Volatile.Read(ref _served);

        public SequencedETagHandler(params string[] payloads)
        {
            _payloads = payloads;
            InnerHandler = new HttpClientHandler();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _served) - 1;

            HttpResponseMessage response;

            if (index < _payloads.Length)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_payloads[index], Encoding.UTF8, "application/json")
                };
            }
            else if (request.Headers.IfNoneMatch.Any())
            {
                response = new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_payloads[_payloads.Length - 1], Encoding.UTF8, "application/json")
                };
            }

            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"sequenced\"");

            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Records features as soon as it is asked to, but holds the first caller there until released -
    /// the suspension point the finding describes.
    /// </summary>
    private sealed class GatedCache : IGrowthBookFeatureCache
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private IDictionary<string, Feature> _features = new Dictionary<string, Feature>();
        private int _writes;

        public ManualResetEventSlim FirstWriteStarted { get; } = new ManualResetEventSlim(false);

        public int FeatureCount => _features.Count;

        public bool IsCacheExpired => false;

        public void ReleaseFirstWrite() => _release.Set();

        public void Clear() => _features = new Dictionary<string, Feature>();

        public Task<IDictionary<string, Feature>> GetFeatures(CancellationToken? cancellationToken = null) =>
            Task.FromResult(_features);

        public async Task RefreshWith(IDictionary<string, Feature> features, CancellationToken? cancellationToken = null)
        {
            _features = features;

            if (Interlocked.Increment(ref _writes) == 1)
            {
                FirstWriteStarted.Set();

                await Task.Run(() => _release.Wait(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Answers 304 to a conditional request and 200 to an unconditional one, as a server does.
    /// </summary>
    private sealed class ConditionalETagHandler : DelegatingHandler
    {
        private readonly string _etag;
        private readonly string _jsonContent;
        private int _requests;

        public ConditionalETagHandler(string etag, string jsonContent)
        {
            _etag = etag;
            _jsonContent = jsonContent;
            InnerHandler = new HttpClientHandler();
        }

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);

            HttpResponseMessage response;

            if (request.Headers.IfNoneMatch.Any())
            {
                response = new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_jsonContent, Encoding.UTF8, "application/json")
                };
            }

            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{_etag}\"");

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ANotModifiedResponseRestartsTheWindowOnAnyCacheThatSupportsIt()
    {
        var etag = "test-etag-304-injected";
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });
        var handler = new ETagTestDelegatingHandler(etag, json, returnNotModifiedOnSecondCall: true);

        var cachePath = Path.Combine(Path.GetTempPath(), $"gb-worker-{Guid.NewGuid():N}");

        try
        {
            // A real injected cache rather than a substitute, because the defect was a type check that
            // a substitute would have satisfied or missed for the wrong reason.
            var cache = new FileBasedFeatureCache("worker-client", cacheExpirationInSeconds: 60, cachePath: cachePath);
            var worker = new FeatureRefreshWorker(_logger, new ETagHttpClientFactory(handler), _config, cache);

            await worker.RefreshCacheFromApi();

            typeof(FileBasedFeatureCache).Should().BeAssignableTo<IFeatureCacheExpiration>(
                "the worker finds the capability by interface, not by concrete type");

            // Expire it, then let the server answer 304.
            cache.SetCacheKey("worker-client-elsewhere");
            cache.SetCacheKey("worker-client");
            cache.IsCacheExpired.Should().BeTrue("nothing has been cached for this key since the reset");

            await worker.RefreshCacheFromApi();

            cache.IsCacheExpired.Should().BeFalse(
                "the server confirmed what is cached is current, so the next load must not go back out");
        }
        finally
        {
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NotModifiedResponseReturnsCachedFeaturesWithoutReplacingCache()
    {
        var etag = "test-etag-304";
        var json = JsonConvert.SerializeObject(new FeaturesResponse { Features = _availableFeatures });
        var handler = new ETagTestDelegatingHandler(etag, json, returnNotModifiedOnSecondCall: true);
        var worker = new FeatureRefreshWorker(_logger, new ETagHttpClientFactory(handler), _config, _cache);

        _cache
            .RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>())
            .Returns(Task.CompletedTask);
        _cache
            .GetFeatures(Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(_availableFeatures));
        // A cache that answers with features reports holding them; the worker reads this to tell a
        // genuine "nothing changed" from a 304 arriving at a cache that has since been emptied.
        _cache.FeatureCount.Returns(_availableFeatures.Count);

        var first = await worker.RefreshCacheFromApi();
        var second = await worker.RefreshCacheFromApi();

        first.Should().BeEquivalentTo(_availableFeatures);
        second.Should().BeEquivalentTo(_availableFeatures);
        handler.ReceivedIfNoneMatchHeader.Should().BeTrue();
        await _cache.Received(1).RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>());
        await _cache.Received(1).GetFeatures(Arg.Any<CancellationToken?>());
    }
}
