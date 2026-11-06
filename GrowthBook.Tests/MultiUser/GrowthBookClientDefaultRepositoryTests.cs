using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

    /// <summary>
    /// With background sync on, an update from the streaming endpoint has to reach evaluation. It used to stop at
    /// the repository's cache, which the client never reads, so a long-running app kept its startup flags.
    /// </summary>
    [Fact]
    public async Task AStreamedUpdateReachesTheClient()
    {
        using var handler = new StreamingHandler(_ => Task.FromResult(FlagOff));
        using var client = CreateClient(handler, cacheExpirationInSeconds: 60, backgroundSync: true);

        await client.InitializeAsync();
        client.IsOn("flag", User).Should().BeFalse();

        handler.Stream(FlagOn);

        SpinWait.SpinUntil(() => client.IsOn("flag", User), TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the streamed update turned the flag on, and the client has to serve it without a manual refresh");
    }

    /// <summary>
    /// A refresh started before a streamed update arrived carries older definitions, however late it is answered.
    /// </summary>
    [Fact]
    public async Task ARefreshAnsweredAfterAStreamedUpdateDoesNotReplaceIt()
    {
        using var refreshMayAnswer = new SemaphoreSlim(0);
        using var refreshStarted = new SemaphoreSlim(0);

        using var handler = new StreamingHandler(async request =>
        {
            if (request == 2)
            {
                refreshStarted.Release();
                await refreshMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return FlagOff;
        });

        using var client = CreateClient(handler, cacheExpirationInSeconds: 60, backgroundSync: true);

        await client.InitializeAsync();

        var refresh = Task.Run(() => client.RefreshFeaturesAsync());
        (await refreshStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        handler.Stream(FlagOn);
        SpinWait.SpinUntil(() => client.IsOn("flag", User), TimeSpan.FromSeconds(10)).Should().BeTrue();

        refreshMayAnswer.Release();
        await refresh;

        client.IsOn("flag", User).Should().BeTrue("the refresh started before the update arrived, so it brings back older definitions");
    }

    /// <summary>
    /// An overtaken refresh must not leave its older flags in the cache either, or the next load reads them back
    /// and installs them over the newer ones.
    /// </summary>
    [Fact]
    public async Task ALoadAfterAnOvertakenRefreshDoesNotReinstallIt()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        var handler = new ScriptedHandler(async request =>
        {
            if (request == 2)
            {
                olderStarted.Release();
                await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                return FlagOff;
            }

            return request == 1 ? FlagOff : FlagOn;
        });

        using var client = CreateClient(handler, cacheExpirationInSeconds: 60);

        await client.InitializeAsync();

        var older = Task.Run(() => client.RefreshFeaturesAsync());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await client.RefreshFeaturesAsync();

        olderMayAnswer.Release();
        await older;

        await client.InitializeAsync();

        handler.Requests.Should().Be(3, "the last load read the cache rather than fetching again");
        client.IsOn("flag", User).Should().BeTrue("the cache has to hold the newer flags, not the overtaken ones");
    }

    /// <summary>
    /// An older response landing after a streamed update must not overwrite the update in the cache.
    /// </summary>
    [Fact]
    public async Task ALoadAfterAnOlderRefreshKeepsTheStreamedUpdate()
    {
        using var refreshMayAnswer = new SemaphoreSlim(0);
        using var refreshStarted = new SemaphoreSlim(0);

        using var handler = new StreamingHandler(async request =>
        {
            if (request == 2)
            {
                refreshStarted.Release();
                await refreshMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return FlagOff;
        });

        using var client = CreateClient(handler, cacheExpirationInSeconds: 60, backgroundSync: true);

        await client.InitializeAsync();

        var refresh = Task.Run(() => client.RefreshFeaturesAsync());
        (await refreshStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        handler.Stream(FlagOn);
        SpinWait.SpinUntil(() => client.IsOn("flag", User), TimeSpan.FromSeconds(10)).Should().BeTrue();

        refreshMayAnswer.Release();
        await refresh;

        await client.InitializeAsync();

        client.IsOn("flag", User).Should().BeTrue("the cache still holds the streamed update, not the older response");
    }

    /// <summary>
    /// An overtaken refresh must not leave its ETag behind either. The cache would hold the newer flags under the
    /// older response's ETag, and once the API rolled back to that response, every refresh would get a 304.
    /// </summary>
    [Fact]
    public async Task ARollbackAfterAnOvertakenRefreshIsApplied()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        var handler = new ETagHandler(async request =>
        {
            switch (request)
            {
                case 1:
                    return (FlagOff, "\"v0\"");
                case 2:
                    olderStarted.Release();
                    await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                    return (FlagOff, "\"v1\"");
                case 3:
                    return (FlagOn, "\"v2\"");
                default:
                    // The API has rolled back to what the overtaken refresh fetched.
                    return (FlagOff, "\"v1\"");
            }
        });

        using var client = CreateClient(handler, cacheExpirationInSeconds: 60);

        await client.InitializeAsync();

        var older = Task.Run(() => client.RefreshFeaturesAsync());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await client.RefreshFeaturesAsync();

        olderMayAnswer.Release();
        await older;

        await client.RefreshFeaturesAsync();

        client.IsOn("flag", User).Should().BeFalse("the API rolled back, and the refresh has to fetch it rather than get a 304");
    }

    private static GrowthBookClient CreateClient(HttpMessageHandler handler, int cacheExpirationInSeconds, bool backgroundSync = false) =>
        new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            ApiHost = "https://growthbook.test",
            LoggerFactory = NullLoggerFactory.Instance,
            CacheExpirationInSeconds = cacheExpirationInSeconds,
            BackgroundSync = backgroundSync,
            HttpClientFactory = new ScriptedHttpClientFactory(handler)
        });

    /// <summary>
    /// Answers the features endpoint with the scripted payload and says streaming is supported; answers the
    /// streaming endpoint with whatever the test streams, holding the connection open in between.
    /// </summary>
    private sealed class StreamingHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<string>> _respond;
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _closed = new CancellationTokenSource();
        private int _requests;

        public StreamingHandler(Func<int, Task<string>> respond) => _respond = respond;

        public void Stream(string payload) => _events.Writer.TryWrite($"data: {payload}\n\n");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath.Contains("/sub/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EventStream(_events.Reader, _closed.Token))
                };
            }

            var json = await _respond(Interlocked.Increment(ref _requests));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            response.Headers.Add(HttpHeaders.ServerSentEvents.Key, HttpHeaders.ServerSentEvents.EnabledValue);

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            _closed.Cancel();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A response body that hands out each streamed event as it arrives and ends once the handler is disposed.
    /// </summary>
    private sealed class EventStream : Stream
    {
        private readonly ChannelReader<string> _events;
        private readonly CancellationToken _closed;
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;

        public EventStream(ChannelReader<string> events, CancellationToken closed)
        {
            _events = events;
            _closed = closed;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset >= _pending.Length)
            {
                try
                {
                    _pending = Encoding.UTF8.GetBytes(await _events.ReadAsync(_closed));
                    _offset = 0;
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            var read = Math.Min(count, _pending.Length - _offset);

            Array.Copy(_pending, _offset, buffer, offset, read);
            _offset += read;

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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

    /// <summary>
    /// Answers each request with what the script returns for its number, and with a 304 when the request already
    /// holds that response's ETag.
    /// </summary>
    private sealed class ETagHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<(string Json, string ETag)>> _respond;
        private int _requests;

        public ETagHandler(Func<int, Task<(string Json, string ETag)>> respond) => _respond = respond;

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
