using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.SSE;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class SSEClientTests
{
    private sealed class TestMessageHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public TestMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class SequencedMessageHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
        private int _index = 0;

        public SequencedMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var i = Math.Min(Interlocked.Increment(ref _index) - 1, _responses.Length - 1);
            return Task.FromResult(_responses[i](request));
        }
    }

    private sealed class CapturingSequencedHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
        private int _index = 0;
        public readonly ConcurrentQueue<HttpRequestMessage> Requests = new();

        public CapturingSequencedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var i = Math.Min(Interlocked.Increment(ref _index) - 1, _responses.Length - 1);
            return Task.FromResult(_responses[i](request));
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public TestHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, false);
        }
    }

    private static SSEClient CreateClient(HttpMessageHandler handler)
    {
        var logger = Substitute.For<ILogger<SSEClient>>();
        var factory = new TestHttpClientFactory(handler);
        // endpoint is irrelevant for handler-based client
        return new SSEClient(logger, factory, "https://example.test/stream", null, ConfiguredClients.ServerSentEventsApiClient);
    }

    [Fact]
    public async Task ConnectAsync_WithNonSuccessResponse_RaisesConnectionErrorAndCancelsCleanly()
    {
        var errorHandlerCalled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new TestMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = CreateClient(handler);

        client.ConnectionError += ex => errorHandlerCalled.TrySetResult(ex);

        using var cts = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(cts.Token);

        var signaled = await Task.WhenAny(errorHandlerCalled.Task, Task.Delay(TimeSpan.FromSeconds(2))) == errorHandlerCalled.Task;
        signaled.Should().BeTrue("connection error should be raised on non-success status code");

        cts.Cancel();
        await connectTask; // should complete after cancellation

        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task ProcessStream_GeneralHandlerException_IsSwallowedAndDoesNotCrash()
    {
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A minimal valid SSE stream with a single data event
        var content = new StringContent("data: hello\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new TestMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        using var client = CreateClient(handler);

        client.ConnectionStatusChanged += status =>
        {
            if (status == SSEConnectionStatus.Connected)
            {
                connectedTcs.TrySetResult(true);
            }
        };

        client.AddEventListener(null, _ => throw new InvalidOperationException("handler failure"));

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        var connected = await Task.WhenAny(connectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(2))) == connectedTcs.Task;
        connected.Should().BeTrue("client should reach Connected state");

        // Give a brief moment for the handler to run and throw inside the client
        await Task.Delay(100);

        // Cancellation should still complete gracefully
        cts.Cancel();
        await connectTask;

        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task RetryOnlyEvent_DoesNotInvokeGeneralHandler()
    {
        var callCount = 0;

        // retry-only event followed by a data event
        var ssePayload = new StringBuilder()
            .AppendLine("retry: 1")
            .AppendLine()
            .AppendLine("data: second")
            .AppendLine()
            .ToString();

        var content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream");
        var handler = new TestMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        using var client = CreateClient(handler);
        client.AddEventListener(null, _ =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        // Wait briefly for processing
        await Task.Delay(200);
        cts.Cancel();
        await connectTask;

        callCount.Should().Be(1, "retry-only event must not trigger general handler");
    }

    [Fact]
    public async Task StreamEnd_TriggersReconnectStatus()
    {
        var statusTcs = new TaskCompletionSource<SSEConnectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First response: short valid SSE stream that ends; Second response: 500 to force error path
        var firstContent = new StringContent("data: first\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new SequencedMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = firstContent },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );

        using var client = CreateClient(handler);

        client.ConnectionStatusChanged += status =>
        {
            if (status == SSEConnectionStatus.Reconnecting)
            {
                statusTcs.TrySetResult(status);
            }
        };

        using var cts = new CancellationTokenSource();
        var task = client.ConnectAsync(cts.Token);

        var sawReconnecting = await Task.WhenAny(statusTcs.Task, Task.Delay(TimeSpan.FromSeconds(3))) == statusTcs.Task;
        sawReconnecting.Should().BeTrue("client should attempt to reconnect after stream ends/error");

        cts.Cancel();
        await task;
    }

    [Fact]
    public async Task SpecificEventHandlerException_IsSwallowedAndDoesNotCrash()
    {
        // SSE with a specific event type
        var content = new StringContent("event: message\ndata: hi\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new TestMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        using var client = CreateClient(handler);

        client.AddEventListener("message", _ => throw new InvalidOperationException("boom"));

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        // Allow handler to execute
        await Task.Delay(150);

        cts.Cancel();
        await connectTask;

        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task StatusSequence_Connect_Then_Reconnect_OnStreamEnd()
    {
        var statuses = new ConcurrentQueue<SSEConnectionStatus>();

        var content = new StringContent("data: first\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new SequencedMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );

        using var client = CreateClient(handler);
        client.ConnectionStatusChanged += s => statuses.Enqueue(s);

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        await Task.Delay(400);
        cts.Cancel();
        await connectTask;

        statuses.Should().Contain(SSEConnectionStatus.Connecting);
        statuses.Should().Contain(SSEConnectionStatus.Connected);
        statuses.Should().Contain(SSEConnectionStatus.Reconnecting);
    }

    [Fact]
    public async Task LastEventId_Header_Is_Sent_On_Reconnect()
    {
        var secondRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstStream = new StringContent("id: 123\ndata: hello\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new CapturingSequencedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = firstStream },
            req =>
            {
                if (req.Headers.TryGetValues("Last-Event-ID", out var values) && values is not null && values.Contains("123"))
                {
                    secondRequestSeen.TrySetResult(true);
                }
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
        );

        using var client = CreateClient(handler);

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        var ok = await Task.WhenAny(secondRequestSeen.Task, Task.Delay(TimeSpan.FromSeconds(3))) == secondRequestSeen.Task;
        ok.Should().BeTrue("Last-Event-ID should be attached on reconnect when event id was received");

        cts.Cancel();
        await connectTask;
    }

    [Fact]
    public async Task ConnectionError_Event_Raised_On_Reconnect_Failure()
    {
        var errorTcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        var content = new StringContent("data: first\n\n", Encoding.UTF8, "text/event-stream");
        var handler = new SequencedMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content },
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        );

        using var client = CreateClient(handler);
        client.ConnectionError += ex => errorTcs.TrySetResult(ex);

        using var cts = new CancellationTokenSource();
        var task = client.ConnectAsync(cts.Token);

        var gotError = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(3))) == errorTcs.Task;
        gotError.Should().BeTrue("error should be raised when reconnect attempt fails");

        cts.Cancel();
        await task;
    }

    [Fact]
    public async Task Cancel_During_Connecting_Disconnects_Cleanly()
    {
        // Make the handler block by never returning, but we can't hang tests.
        // Instead, use a handler that returns after a delay so we can cancel before it.
        var handler = new TestMessageHandler(_ =>
        {
            Thread.Sleep(200); // give time to cancel while in Connecting
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\n\n") };
        });

        using var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var connectTask = client.ConnectAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await connectTask;

        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }

    [Fact]
    public async Task Cancel_During_Reconnecting_Disconnects_Cleanly()
    {
        // First call fails fast to enter Reconnecting, then we cancel during delay
        var handler = new TestMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var client = CreateClient(handler);

        using var cts = new CancellationTokenSource();
        var connectTask = client.ConnectAsync(cts.Token);

        // Wait a bit to ensure it hits Reconnecting state and is awaiting delay
        await Task.Delay(50);
        cts.Cancel();
        await connectTask;

        client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected);
    }
}


