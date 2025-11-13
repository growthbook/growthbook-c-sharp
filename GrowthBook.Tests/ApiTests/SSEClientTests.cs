using System;
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
    }
}


