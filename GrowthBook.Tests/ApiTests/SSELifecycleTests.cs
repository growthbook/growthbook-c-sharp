using System;
using System.Collections.Generic;
using System.IO;
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
/// Covers the SSE reconnect lifecycle. The repo had no working SSE coverage - the one integration test
/// is skipped - so these drive <see cref="SSEClient"/> directly through a controllable fake transport.
/// </summary>
public class SSELifecycleTests
{
    /// <summary>
    /// Serves SSE responses on demand so a test can decide what each connection attempt does: end the
    /// stream cleanly, fail outright, or deliver data. Records the time of every attempt so tests can
    /// assert on the gap between reconnects.
    /// </summary>
    private sealed class ScriptedSseHandler : HttpMessageHandler
    {
        private readonly Func<int, string> _bodyForAttempt;

        public ScriptedSseHandler(Func<int, string> bodyForAttempt) => _bodyForAttempt = bodyForAttempt;

        public List<DateTime> AttemptTimestamps { get; } = new List<DateTime>();
        public int AttemptCount => AttemptTimestamps.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (AttemptTimestamps)
            {
                AttemptTimestamps.Add(DateTime.UtcNow);
            }

            var body = _bodyForAttempt(AttemptCount);

            if (body == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            // An empty body is a stream the server accepted and closed immediately - the case that used to
            // spin the reconnect loop with no delay.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
            });
        }
    }

    private sealed class SingleHandlerHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure)
            => configure(new HttpClient(_handler, disposeHandler: false));
    }

    private static SSEClient CreateClient(HttpMessageHandler handler)
        => new SSEClient(
            NullLogger<SSEClient>.Instance,
            new SingleHandlerHttpClientFactory(handler),
            "http://localhost/sub/test-key",
            null,
            ConfiguredClients.ServerSentEventsApiClient);

    private static int GetCurrentRetryAttempt(SSEClient client)
        => (int)typeof(SSEClient)
            .GetField("_currentRetryAttempt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(client);

    /// <summary>
    /// Delivers a payload on the first read and then throws, standing in for the ordinary case of a
    /// connection that streams fine for a while before the network drops it.
    /// </summary>
    private sealed class DataThenErrorStream : Stream
    {
        private readonly byte[] _payload;
        private bool _delivered;

        public DataThenErrorStream(string payload) => _payload = Encoding.UTF8.GetBytes(payload);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delivered)
            {
                throw new IOException("connection dropped after streaming");
            }

            _delivered = true;

            var length = Math.Min(count, _payload.Length);
            Array.Copy(_payload, 0, buffer, offset, length);

            return length;
        }

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

    private sealed class DataThenErrorHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private int _attemptCount;

        public DataThenErrorHandler(string payload) => _payload = payload;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attemptCount);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DataThenErrorStream(_payload))
            });
        }
    }

    [Fact]
    public async Task AConnectionThatDeliveredDataBeforeDroppingRestartsTheBackoff()
    {
        // The regression the reviewer caught: the retry counter used to be reset only when
        // ProcessStreamAsync *returned*, so a connection that streamed successfully and then threw never
        // reset it. Ordinary network drops would then accumulate - even days apart - until the client hit
        // _maxRetryAttempts and stopped streaming for good.
        //
        // `retry: 1` shrinks the backoff so several cycles fit inside the test.
        var handler = new DataThenErrorHandler("retry: 1\ndata: {\"features\":{}}\n\n");
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(cancellation.Token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.AttemptCount < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var retryAttemptAfterSeveralDrops = GetCurrentRetryAttempt(client);

        cancellation.Cancel();

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
        }

        handler.AttemptCount.Should().BeGreaterThanOrEqualTo(4, "because each productive-then-dropped connection should be retried");
        retryAttemptAfterSeveralDrops.Should().Be(1,
            "because every connection delivered data, so the counter must be reset each time instead of climbing towards the give-up limit");
        client.ConnectionStatus.Should().NotBe(SSEConnectionStatus.Failed, "because the client must not give up on connections that are actually working");
    }

    [Fact]
    public async Task ACleanStreamCloseDoesNotCauseAHotReconnectLoop()
    {
        // Every attempt is accepted and closed straight away. Before the fix this ran as fast as the
        // transport could answer, because a clean close raised no exception and the retry counter was
        // reset on every successful connect, so the backoff never applied.
        var handler = new ScriptedSseHandler(_ => string.Empty);
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(cancellation.Token);

        await Task.Delay(1500);
        cancellation.Cancel();

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling mid-backoff.
        }

        // The first reconnect waits out `_retryTimeMs * 2^1` plus jitter, i.e. at least 6s, so within 1.5s
        // exactly one attempt is possible. Before the fix this number ran into the thousands.
        handler.AttemptCount.Should().Be(1,
            $"because a backoff has to sit between reconnects, but the transport was hit {handler.AttemptCount} times in 1.5s");
    }

    [Fact]
    public async Task CancellingStopsReconnectingAndReportsDisconnected()
    {
        var handler = new ScriptedSseHandler(_ => string.Empty);
        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(cancellation.Token);
        await Task.Delay(200);

        cancellation.Cancel();

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
        }

        var attemptsAtCancellation = handler.AttemptCount;

        await Task.Delay(1500);

        handler.AttemptCount.Should().Be(attemptsAtCancellation, "because cancellation must stop the reconnect loop for good");
        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task DisconnectStopsAnActiveConnection()
    {
        var handler = new ScriptedSseHandler(_ => string.Empty);
        using var client = CreateClient(handler);

        var connectTask = client.ConnectAsync(CancellationToken.None);
        await Task.Delay(200);

        client.Disconnect();

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
        }

        var attemptsAtDisconnect = handler.AttemptCount;

        await Task.Delay(1500);

        handler.AttemptCount.Should().Be(attemptsAtDisconnect, "because Disconnect has to stop the reconnect loop, not just the current request");
        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task DeliveredEventsStillReachListeners()
    {
        // Guards the fix against over-correcting: reconnect throttling must not stop real events arriving.
        var handler = new ScriptedSseHandler(attempt => attempt == 1
            ? "data: {\"features\":{}}\n\n"
            : string.Empty);

        using var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();

        var received = new List<string>();
        var receivedSignal = new ManualResetEventSlim(false);

        client.AddEventListener(null, sseEvent =>
        {
            if (sseEvent.HasData)
            {
                received.Add(sseEvent.Data);
                receivedSignal.Set();
            }

            return Task.CompletedTask;
        });

        var connectTask = client.ConnectAsync(cancellation.Token);

        receivedSignal.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("because the event on the first attempt should be delivered");
        cancellation.Cancel();

        try
        {
            await connectTask;
        }
        catch (OperationCanceledException)
        {
        }

        received.Should().ContainSingle();
        received[0].Should().Contain("features");
    }
}
