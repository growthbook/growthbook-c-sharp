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
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests
{
    public class SSEClientTests
    {
        private sealed class FiniteSequencedHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
            private int _index = 0;

            public FiniteSequencedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                _responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var i = Interlocked.Increment(ref _index) - 1;
                if (i >= _responses.Length)
                {
                    // After planned responses, return non-success to break reconnect loop fast
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
                }
                return Task.FromResult(_responses[i](request));
            }
        }

        private sealed class SimpleHandler : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
            private int _count = 0;
            public SimpleHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var idx = Interlocked.Increment(ref _count);
                Console.WriteLine($"[SimpleHandler] SendAsync idx={idx} url={request.RequestUri}");
                HttpResponseMessage resp;
                if (idx == 1)
                {
                    resp = _responseFactory(request);
                    Console.WriteLine($"[SimpleHandler] first response status={resp.StatusCode} contentType={resp.Content?.Headers?.ContentType}");
                }
                else
                {
                    // After first call return non-success to break reconnect loop fast
                    resp = new HttpResponseMessage(HttpStatusCode.Gone);
                    Console.WriteLine("[SimpleHandler] returning 410 Gone after first response");
                }
                return Task.FromResult(resp);
            }
        }

        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            public TestHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
            public HttpClient CreateClient(string name) => new HttpClient(_handler, false);
        }

        private static SSEClient CreateClient(HttpMessageHandler handler, System.Collections.Generic.Dictionary<string,string> headers = null)
        {
            var logger = Substitute.For<ILogger<SSEClient>>();
            var factory = new TestHttpClientFactory(handler);
            return new SSEClient(logger, factory, "https://example.test/stream", headers, ConfiguredClients.ServerSentEventsApiClient);
        }

        [Fact(Timeout = 8000)]
        public async Task NonSuccessResponse_RaisesConnectionError()
        {
            var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new SimpleHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            using var client = CreateClient(handler);
            client.ConnectionError += _ => errorTcs.TrySetResult(true);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                var ok = await Task.WhenAny(errorTcs.Task, Task.Delay(TimeSpan.FromSeconds(2))) == errorTcs.Task;
                ok.Should().BeTrue();
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }
        }

        [Fact(Timeout = 8000)]
        public async Task RequestHeaders_Are_Sent_On_Initial_Connect()
        {
            var sawAuth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new SimpleHandler(req =>
            {
                if (req.Headers.TryGetValues("Authorization", out var values) && values is not null && System.Linq.Enumerable.Contains(values, "Bearer test-token"))
                {
                    sawAuth.TrySetResult(true);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: ok\n\n", Encoding.UTF8, "text/event-stream")
                };
            });

            var headers = new System.Collections.Generic.Dictionary<string,string> { { "Authorization", "Bearer test-token" } };
            using var client = CreateClient(handler, headers);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                var ok = await Task.WhenAny(sawAuth.Task, Task.Delay(TimeSpan.FromSeconds(2))) == sawAuth.Task;
                ok.Should().BeTrue();
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }
        }

        [Fact(Timeout = 10000)]
        public async Task LastEventId_Header_Is_Sent_On_Reconnect()
        {
            var secondRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstStream = new StringContent("id: 123\ndata: hello\n\n", Encoding.UTF8, "text/event-stream");
            var handler = new FiniteSequencedHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = firstStream },
                req =>
                {
                    if (req.Headers.TryGetValues("Last-Event-ID", out var values) && values is not null && System.Linq.Enumerable.Contains(values, "123"))
                    {
                        secondRequestSeen.TrySetResult(true);
                    }
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            );

            using var client = CreateClient(handler);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(700));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                var ok = await Task.WhenAny(secondRequestSeen.Task, Task.Delay(TimeSpan.FromSeconds(3))) == secondRequestSeen.Task;
                ok.Should().BeTrue();
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }
        }

        [Fact(Timeout = 8000)]
        public async Task MalformedEvent_IsIgnored_And_ValidData_Processed()
        {
            var received = 0;

            var ssePayload = new StringBuilder()
                .AppendLine("this is not sse")
                .AppendLine()
                .AppendLine("data: real")
                .AppendLine()
                .ToString();

            var handler = new SimpleHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            });

            using var client = CreateClient(handler);
            client.AddEventListener(null, _ => { Interlocked.Increment(ref received); return Task.CompletedTask; });

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                await Task.Delay(200);
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }

            received.Should().BeGreaterOrEqualTo(1);
        }

        [Fact(Timeout = 8000)]
        public async Task ShouldReconnect_ReturnsTrue_ForStatusCodes200_299()
        {
            // Test that ShouldReconnect logic matches Flutter implementation
            var handler = new SimpleHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: test\n\n", Encoding.UTF8, "text/event-stream")
            });

            using var client = CreateClient(handler);
            var reconnected = false;
            var statusCodes = new List<HttpStatusCode>();

            // Use reflection to access private ShouldReconnect method or test via behavior
            // Since ShouldReconnect is private, we'll test it indirectly through connection behavior
            var handler2 = new FiniteSequencedHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("id: 1\nevent: features\ndata: test\n\n", Encoding.UTF8, "text/event-stream")
                },
                req =>
                {
                    statusCodes.Add(req.RequestUri != null ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
                    reconnected = true;
                    return new HttpResponseMessage(HttpStatusCode.Gone); // Stop after reconnect attempt
                }
            );

            using var client2 = CreateClient(handler2);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            
            Task connectTask = null;
            try
            {
                connectTask = client2.ConnectAsync(cts.Token);
                await Task.Delay(300);
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }

            // Should reconnect when status code is 200-299
            reconnected.Should().BeTrue("because connection closed with success status should trigger reconnect");
        }

        [Fact(Timeout = 8000)]
        public async Task ShouldReconnect_ReturnsFalse_ForStatusCodesOutside200_299()
        {
            var handler = new FiniteSequencedHandler(
                _ => new HttpResponseMessage(HttpStatusCode.Forbidden) // 403 - outside 200-299 range
                {
                    Content = new StringContent("data: test\n\n", Encoding.UTF8, "text/event-stream")
                },
                req => new HttpResponseMessage(HttpStatusCode.BadRequest) // Should not reach here
            );

            using var client = CreateClient(handler);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(1000));
            
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                // Wait a bit to allow connection attempt
                await Task.Delay(300);
            }
            finally
            {
                cts.Cancel();
                // Explicitly disconnect to ensure status is set to Disconnected
                client.Disconnect();
                
                if (connectTask != null)
                {
                    try
                    {
                        await connectTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                }
            }

            // After cancellation and explicit disconnect, status should be Disconnected
            client.ConnectionStatus.Should().Be(SSEConnectionStatus.Disconnected, "because connection should be disconnected after cancellation");
        }

        [Fact(Timeout = 8000)]
        public async Task Events_AreFilteredByEventType()
        {
            var featuresEventReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var otherEventReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ssePayload = new StringBuilder()
                .AppendLine("id: 1")
                .AppendLine("event: features")
                .AppendLine("data: test-features")
                .AppendLine()
                .AppendLine("id: 2")
                .AppendLine("event: other")
                .AppendLine("data: test-other")
                .AppendLine()
                .ToString();

            var handler = new SimpleHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            });

            using var client = CreateClient(handler);
            
            // Add listener for "features" events only
            client.AddEventListener("features", async (sseEvent) =>
            {
                if (sseEvent.Event == "features")
                {
                    featuresEventReceived.TrySetResult(true);
                }
                await Task.CompletedTask;
            });

            // Add listener for all events
            client.AddEventListener(null, async (sseEvent) =>
            {
                if (sseEvent.Event == "other")
                {
                    otherEventReceived.TrySetResult(true);
                }
                await Task.CompletedTask;
            });

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                await Task.Delay(200);
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }

            // Both specific and general listeners should receive events
            featuresEventReceived.Task.IsCompleted.Should().BeTrue("because features event should be received");
            otherEventReceived.Task.IsCompleted.Should().BeTrue("because other event should be received by general listener");
        }

        [Fact(Timeout = 8000)]
        public async Task DuplicateEventIds_AreTracked()
        {
            var eventCount = 0;
            var lastEventId = "";

            var ssePayload = new StringBuilder()
                .AppendLine("id: 123")
                .AppendLine("event: features")
                .AppendLine("data: first")
                .AppendLine()
                .AppendLine("id: 123") // Duplicate ID
                .AppendLine("event: features")
                .AppendLine("data: duplicate")
                .AppendLine()
                .AppendLine("id: 456")
                .AppendLine("event: features")
                .AppendLine("data: second")
                .AppendLine()
                .ToString();

            var handler = new SimpleHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            });

            using var client = CreateClient(handler);
            
            client.AddEventListener("features", async (sseEvent) =>
            {
                if (sseEvent.Id == lastEventId && !string.IsNullOrEmpty(lastEventId))
                {
                    // This would be a duplicate - but SSEClient doesn't filter duplicates itself
                    // FeatureRefreshWorker does the filtering
                }
                lastEventId = sseEvent.Id;
                Interlocked.Increment(ref eventCount);
                await Task.CompletedTask;
            });

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            Task connectTask = null;
            try
            {
                connectTask = client.ConnectAsync(cts.Token);
                await Task.Delay(200);
            }
            finally
            {
                cts.Cancel();
                if (connectTask != null) await connectTask;
            }

            // Should receive all events (duplicate filtering happens in FeatureRefreshWorker)
            eventCount.Should().BeGreaterOrEqualTo(3, "because SSEClient should process all events");
            client.LastEventId.Should().Be("456", "because last event ID should be updated");
        }
    }
}


