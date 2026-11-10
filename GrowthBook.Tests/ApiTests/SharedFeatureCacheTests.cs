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
/// Workers that write to one cache, as the instances a factory creates from a context with a cache each build. What
/// the cache holds has to move only forward, whichever worker's response lands last.
/// </summary>
public class SharedFeatureCacheTests
{
    private const string FlagOff = @"{ ""features"": { ""flag"": { ""defaultValue"": false } } }";
    private const string FlagOn = @"{ ""features"": { ""flag"": { ""defaultValue"": true } } }";

    [Fact]
    public async Task AnOlderResponseFromOneWorkerDoesNotReplaceAnothersNewerOne()
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
                default:
                    return (FlagOn, "\"v2\"");
            }
        });

        var cache = new InMemoryFeatureCache(60);
        using var first = CreateWorker(handler, cache);
        using var second = CreateWorker(handler, cache);

        var older = Task.Run(() => first.RefreshCacheFromApi());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await second.RefreshCacheFromApi();

        olderMayAnswer.Release();
        await older;

        IsOn(await cache.GetFeatures()).Should().BeTrue("the other worker's newer response is what the cache has to keep");

        // The API still has the newer payload. Asked with the ETag of what the cache holds, it answers 304.
        await second.RefreshCacheFromApi();

        IsOn(await cache.GetFeatures()).Should().BeTrue("a 304 must confirm the payload the cache holds, not an older one written over it");
    }

    private static bool IsOn(IDictionary<string, Feature> features) => features["flag"].DefaultValue.ToObject<bool>();

    private static FeatureRefreshWorker CreateWorker(HttpMessageHandler handler, IGrowthBookFeatureCache cache) =>
        new FeatureRefreshWorker(
            NullLogger<FeatureRefreshWorker>.Instance,
            new ScriptedHttpClientFactory(handler),
            new GrowthBookConfigurationOptions { ApiHost = "https://growthbook.test", ClientKey = "sdk-test" },
            cache);

    /// <summary>
    /// Answers each request with what the script returns for its number, and with a 304 when the request already holds
    /// that answer's ETag.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<(string Json, string ETag)>> _respond;
        private int _requests;

        public ScriptedHandler(Func<int, Task<(string Json, string ETag)>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (json, etag) = await _respond(Interlocked.Increment(ref _requests));
            var tag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);

            if (request.Headers.IfNoneMatch.Contains(tag))
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
