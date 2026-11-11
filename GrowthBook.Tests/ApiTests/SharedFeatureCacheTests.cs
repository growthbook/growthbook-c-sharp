using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.SSE;
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

    /// <summary>
    /// A worker sharing a cache sends the ETag another worker stored, so its very first request can be answered 304.
    /// It still has to open a stream of its own: the other worker's stream ends when its instance is disposed.
    /// </summary>
    [Fact]
    public async Task AWorkerAnswered304OnItsFirstRequestStillOpensItsStream()
    {
        var handler = new StreamingApiHandler();
        var cache = new InMemoryFeatureCache(60);
        using var first = CreateWorker(handler, cache, preferStreaming: true);
        using var second = CreateWorker(handler, cache, preferStreaming: true);

        await first.RefreshCacheFromApi();
        SpinWait.SpinUntil(() => handler.Streams >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue("the first worker opens a stream after its 200");

        await second.RefreshCacheFromApi();

        handler.NotModifiedAnswers.Should().Be(1, "the second worker's first request carried the ETag the first one stored");
        SpinWait.SpinUntil(() => handler.Streams >= 2, TimeSpan.FromSeconds(5)).Should().BeTrue("the second worker has to open a stream of its own");
    }

    /// <summary>
    /// A 304 that leaves the streaming header out says nothing about streaming. Read as "no streaming", it closed a
    /// stream that was working, and flag changes waited for the next HTTP refresh.
    /// </summary>
    [Fact]
    public async Task AnUnchangedResponseWithoutTheStreamingHeaderKeepsTheStreamOpen()
    {
        var handler = new StreamingApiHandler();
        using var worker = CreateWorker(handler, new InMemoryFeatureCache(60), preferStreaming: true);

        await worker.RefreshCacheFromApi();
        SpinWait.SpinUntil(() => handler.Streams >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue("the worker opens a stream after its 200");

        await worker.RefreshCacheFromApi();

        handler.NotModifiedAnswers.Should().Be(1);
        StreamStatus(worker).Should().NotBe(SSEConnectionStatus.Disconnected, "the 304 did not say streaming stopped");
    }

    /// <summary>
    /// What the server said about streaming is kept only with a response the cache accepted. An older reply without
    /// the header, turned away by the ordering check, used to overwrite it, and the newer worker's next 304 without the
    /// header then closed its working stream.
    /// </summary>
    [Fact]
    public async Task AnOlderReplyWithoutTheStreamingHeaderDoesNotStopTheNewerStream()
    {
        using var olderMayAnswer = new SemaphoreSlim(0);
        using var olderStarted = new SemaphoreSlim(0);

        var handler = new ScriptedStreamingHandler(async request =>
        {
            switch (request)
            {
                case 1:
                    olderStarted.Release();
                    await olderMayAnswer.WaitAsync(TimeSpan.FromSeconds(10));
                    return (FlagOff, "\"v1\"", false);
                default:
                    return (FlagOn, "\"v2\"", true);
            }
        });

        var cache = new InMemoryFeatureCache(60);
        using var older = CreateWorker(handler, cache, preferStreaming: true);
        using var newer = CreateWorker(handler, cache, preferStreaming: true);

        var olderRefresh = Task.Run(() => older.RefreshCacheFromApi());
        (await olderStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        await newer.RefreshCacheFromApi();
        SpinWait.SpinUntil(() => handler.Streams >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue("the newer worker opens a stream after its 200");

        olderMayAnswer.Release();
        await olderRefresh;

        await newer.RefreshCacheFromApi();

        handler.NotModifiedAnswers.Should().Be(1, "the newer worker's second request was answered 304, without the header");
        StreamStatus(newer).Should().NotBe(SSEConnectionStatus.Disconnected, "the older reply was turned away, so it says nothing about streaming");
    }

    /// <summary>
    /// The worker's stream status, read by reflection. Disconnecting cancels the stream without closing it, so the
    /// fake API cannot see it happen.
    /// </summary>
    private static SSEConnectionStatus StreamStatus(FeatureRefreshWorker worker)
    {
        var field = typeof(FeatureRefreshWorker).GetField("_sseClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull("the test reads the worker's stream to see whether it was disconnected");

        var client = (SSEClient)field.GetValue(worker);
        client.Should().NotBeNull("the worker opened a stream after its 200");

        return client.ConnectionStatus;
    }

    private static bool IsOn(IDictionary<string, Feature> features) => features["flag"].DefaultValue.ToObject<bool>();

    private static FeatureRefreshWorker CreateWorker(HttpMessageHandler handler, IGrowthBookFeatureCache cache, bool preferStreaming = false) =>
        new FeatureRefreshWorker(
            NullLogger<FeatureRefreshWorker>.Instance,
            new ScriptedHttpClientFactory(handler),
            new GrowthBookConfigurationOptions { ApiHost = "https://growthbook.test", ClientKey = "sdk-test", PreferServerSentEvents = preferStreaming },
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

    /// <summary>
    /// Serves one payload with an ETag and streaming enabled, answers 304 to that ETag without the streaming header, as
    /// some servers and proxies do, and keeps every stream it opens open, counting them.
    /// </summary>
    private sealed class StreamingApiHandler : HttpMessageHandler
    {
        private int _streams;
        private int _notModified;

        public int Streams => Volatile.Read(ref _streams);

        public int NotModifiedAnswers => Volatile.Read(ref _notModified);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath.StartsWith("/sub/"))
            {
                Interlocked.Increment(ref _streams);

                var stream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream()) };
                stream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

                return Task.FromResult(stream);
            }

            var tag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            HttpResponseMessage response;

            if (request.Headers.IfNoneMatch.Contains(tag))
            {
                Interlocked.Increment(ref _notModified);
                response = new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FlagOn, Encoding.UTF8, "application/json") };
                response.Headers.Add("x-sse-support", "enabled");
            }

            response.Headers.ETag = tag;

            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A stream that never delivers anything, as a quiet event stream does, until it is disposed.
    /// </summary>
    private sealed class OpenStream : System.IO.Stream
    {
        private readonly TaskCompletionSource<int> _closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _closed.TrySetResult(0));

            return _closed.Task;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult(0);
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Answers each features request with what the script returns for its number, sending the streaming header only
    /// when the script says so, and 304 without it to a request holding that answer's ETag. Streams are kept open.
    /// </summary>
    private sealed class ScriptedStreamingHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<(string Json, string ETag, bool Streams)>> _respond;
        private int _requests;
        private int _streams;
        private int _notModified;

        public ScriptedStreamingHandler(Func<int, Task<(string Json, string ETag, bool Streams)>> respond) => _respond = respond;

        public int Streams => Volatile.Read(ref _streams);

        public int NotModifiedAnswers => Volatile.Read(ref _notModified);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath.StartsWith("/sub/"))
            {
                Interlocked.Increment(ref _streams);

                var stream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream()) };
                stream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

                return stream;
            }

            var (json, etag, streams) = await _respond(Interlocked.Increment(ref _requests));
            var tag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);

            if (request.Headers.IfNoneMatch.Contains(tag))
            {
                Interlocked.Increment(ref _notModified);

                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = tag;

                return notModified;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.ETag = tag;

            if (streams)
            {
                response.Headers.Add("x-sse-support", "enabled");
            }

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
