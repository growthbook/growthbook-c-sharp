using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Evaluation;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.MultiUser;

/// <summary>
/// Covers the client with the repository it builds for itself, which fetches over HTTP and reports back through
/// the refresh worker's callback. The other client tests supply a repository of their own and never reach it.
/// </summary>
public class GrowthBookClientDefaultRepositoryTests
{
    private const string FlagOff = @"{ ""features"": { ""flag"": { ""defaultValue"": false } } }";
    private const string FlagOn = @"{ ""features"": { ""flag"": { ""defaultValue"": true } } }";

    private static readonly UserContext User = new UserContext { Attributes = JObject.Parse(@"{ ""id"": ""user-1"" }") };

    [Fact]
    public async Task AnOlderRefreshAnsweredLastDoesNotReplaceANewerOne()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        // The first request is held back and answers with the older definitions; the second answers at once
        var handler = new ScriptedHandler(async request =>
        {
            if (request == 1)
            {
                olderStarted.Release();
                await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                return FlagOff;
            }

            return FlagOn;
        });

        using var client = CreateClient(handler, cacheExpirationInSeconds: 60);

        var older = Task.Run(() => client.RefreshFeaturesAsync());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await client.RefreshFeaturesAsync();

        olderMayAnswer.Release();
        await older;

        // Room for anything still reacting to the older answer to finish before the result is read
        await Task.Delay(300);

        client.IsOn("flag", User).Should().BeTrue(
            "the refresh started later carries the newer definitions, however late the earlier one comes back");
    }

    [Fact]
    public async Task ACacheThatExpiresAtOnceDoesNotKeepRefreshingItself()
    {
        var handler = new ScriptedHandler(_ => Task.FromResult(FlagOn));

        using var client = CreateClient(handler, cacheExpirationInSeconds: 0);

        await client.InitializeAsync();

        // A refresh that triggers the next one would keep requesting with nobody asking for it
        await Task.Delay(300);

        handler.Requests.Should().Be(1, "loading once fetches once");
        client.IsOn("flag", User).Should().BeTrue();
    }

    [Fact]
    public async Task InitializingAgainOnAnExpiredCacheInstallsWhatItFetched()
    {
        var handler = new ScriptedHandler(request => Task.FromResult(request == 1 ? FlagOff : FlagOn));

        using var client = CreateClient(handler, cacheExpirationInSeconds: 0);

        await client.InitializeAsync();
        client.IsOn("flag", User).Should().BeFalse();

        await client.InitializeAsync();

        client.IsOn("flag", User).Should().BeTrue(
            "the cache had expired, so the second load fetched again and has to install what came back");
    }

    private static GrowthBookClient CreateClient(HttpMessageHandler handler, int cacheExpirationInSeconds) =>
        new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            ApiHost = "https://growthbook.test",
            LoggerFactory = NullLoggerFactory.Instance,
            CacheExpirationInSeconds = cacheExpirationInSeconds,
            HttpClientFactory = new ScriptedHttpClientFactory(handler)
        });

    /// <summary>
    /// Answers each request with the payload the script returns for its position in the order requests arrived.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<string>> _respond;
        private int _requests;

        public ScriptedHandler(Func<int, Task<string>> respond) => _respond = respond;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await _respond(Interlocked.Increment(ref _requests));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
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
