using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

/// <summary>
/// Overlapping requests on the refresh worker itself. The default repository shares one request between
/// overlapping refreshes, so these cannot happen through it any more, but a repository of the caller's own may
/// still drive the worker concurrently, and an older response landing last must not undo a newer one.
/// </summary>
public class FeatureRefreshWorkerOrderingTests
{
    private const string FlagOff = @"{ ""features"": { ""flag"": { ""defaultValue"": false } } }";
    private const string FlagOn = @"{ ""features"": { ""flag"": { ""defaultValue"": true } } }";

    [Fact]
    public async Task AnOlderResponseAnsweredLastDoesNotReplaceANewerOne()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        var handler = new ScriptedHandler(async request =>
        {
            if (request == 1)
            {
                olderStarted.Release();
                await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                return (FlagOff, null);
            }

            return (FlagOn, null);
        });

        var cache = new InMemoryFeatureCache(60);
        using var worker = CreateWorker(handler, cache);

        var older = Task.Run(() => worker.RefreshCacheFromApi());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await worker.RefreshCacheFromApi();

        olderMayAnswer.Release();
        var olderResult = await older;

        IsOn(await cache.GetFeatures()).Should().BeTrue("the cache has to keep the newer response, however late the older one lands");
        IsOn(olderResult).Should().BeTrue("the overtaken request hands back what the cache holds, not its own older answer");
    }

    /// <summary>
    /// The cache would otherwise hold the newer flags under the older response's ETag, and once the API rolled
    /// back to that response, every refresh would get a 304.
    /// </summary>
    [Fact]
    public async Task AnOvertakenResponseDoesNotLeaveItsETagBehind()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        var handler = new ScriptedHandler(async request =>
        {
            switch (request)
            {
                case 1:
                    olderStarted.Release();
                    await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                    return (FlagOff, "\"v1\"");
                case 2:
                    return (FlagOn, "\"v2\"");
                default:
                    // The API has rolled back to what the overtaken request fetched.
                    return (FlagOff, "\"v1\"");
            }
        });

        var cache = new InMemoryFeatureCache(60);
        using var worker = CreateWorker(handler, cache);

        var older = Task.Run(() => worker.RefreshCacheFromApi());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await worker.RefreshCacheFromApi();

        olderMayAnswer.Release();
        await older;

        await worker.RefreshCacheFromApi();

        IsOn(await cache.GetFeatures()).Should().BeFalse("the API rolled back, and the refresh has to fetch it rather than get a 304");
    }

    private static bool IsOn(IDictionary<string, Feature> features) => features["flag"].DefaultValue.ToObject<bool>();

    private static FeatureRefreshWorker CreateWorker(HttpMessageHandler handler, IGrowthBookFeatureCache cache) =>
        new FeatureRefreshWorker(
            NullLogger<FeatureRefreshWorker>.Instance,
            new ScriptedHttpClientFactory(handler),
            new GrowthBookConfigurationOptions { ApiHost = "https://growthbook.test", ClientKey = "sdk-test" },
            cache);

    /// <summary>
    /// Answers each request with what the script returns for its number. With an ETag, a request that already
    /// holds it gets a 304.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<(string Json, string ETag)>> _respond;
        private int _requests;

        public ScriptedHandler(Func<int, Task<(string Json, string ETag)>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (json, etag) = await _respond(Interlocked.Increment(ref _requests));
            var tag = etag is null ? null : new System.Net.Http.Headers.EntityTagHeaderValue(etag);

            if (tag != null && request.Headers.IfNoneMatch.Contains(tag))
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = tag;

                return notModified;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = tag;

            return response;
        }
    }

    private sealed class ScriptedHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public ScriptedHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure) =>
            configure(new HttpClient(_handler, disposeHandler: false));
    }
}
