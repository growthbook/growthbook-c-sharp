using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowthBook.Tests.MultiUser;

/// <summary>
/// The multi-user client takes the same request headers and streaming host as a <see cref="Context"/>, as
/// <c>apiHostRequestHeaders</c>, <c>streamingHost</c> and <c>streamingHostRequestHeaders</c> do on the JavaScript SDK's
/// client options.
/// </summary>
public class GrowthBookClientRequestHeadersTests
{
    /// <summary>Answers the features request with streaming enabled, keeps every stream open, and records every request.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = new List<HttpRequestMessage>();

        public List<HttpRequestMessage> Requests { get { lock (_requests) return _requests.ToList(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);

            if (request.RequestUri.AbsolutePath.StartsWith("/sub/"))
            {
                var stream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(cancellationToken)) };
                stream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(stream);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""features"":{""flag"":{""defaultValue"":true}}}", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-sse-support", "enabled");

            return Task.FromResult(response);
        }
    }

    private sealed class BlockingStream : System.IO.MemoryStream
    {
        private readonly CancellationToken _token;

        public BlockingStream(CancellationToken token) => _token = token;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken).Token);
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }
    }

    private sealed class HandlerFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public HandlerFactory(HttpMessageHandler handler) => _handler = handler;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure) =>
            configure(new HttpClient(_handler, disposeHandler: false));
    }

    [Fact]
    public async Task TheFeaturesRequestAndTheStreamCarryTheirHeadersToTheirHosts()
    {
        var handler = new RecordingHandler();

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            ApiHost = "https://api.example.test",
            StreamingHost = "https://stream.example.test",
            ApiHostRequestHeaders = new Dictionary<string, string> { ["X-Api"] = "api-value" },
            StreamingHostRequestHeaders = new Dictionary<string, string> { ["X-Stream"] = "stream-value" },
            BackgroundSync = true,
            HttpClientFactory = new HandlerFactory(handler),
            LoggerFactory = NullLoggerFactory.Instance
        });

        (await client.InitializeAsync()).Success.Should().BeTrue();

        SpinWait.SpinUntil(() => handler.Requests.Any(r => r.RequestUri.AbsolutePath.StartsWith("/sub/")), TimeSpan.FromSeconds(5))
            .Should().BeTrue("the API said it streams, so the client opens a stream");

        var features = handler.Requests.First(r => r.RequestUri.AbsolutePath.StartsWith("/api/features/"));
        features.RequestUri.Host.Should().Be("api.example.test");
        features.Headers.GetValues("X-Api").Should().Equal("api-value");
        features.Headers.Contains("X-Stream").Should().BeFalse();

        var stream = handler.Requests.First(r => r.RequestUri.AbsolutePath.StartsWith("/sub/"));
        stream.RequestUri.Host.Should().Be("stream.example.test");
        stream.Headers.GetValues("X-Stream").Should().Equal("stream-value");
        stream.Headers.Contains("X-Api").Should().BeFalse();
    }

    [Theory]
    [InlineData("If-None-Match", null)]
    [InlineData(null, "User-Agent")]
    public void AReservedHeaderIsRejectedWhenTheClientIsCreated(string apiHeader, string streamingHeader)
    {
        var options = new Options
        {
            ClientKey = "sdk-test",
            ApiHostRequestHeaders = apiHeader is null ? null : new Dictionary<string, string> { [apiHeader] = "x" },
            StreamingHostRequestHeaders = streamingHeader is null ? null : new Dictionary<string, string> { [streamingHeader] = "x" },
            LoggerFactory = NullLoggerFactory.Instance
        };

        FluentActions.Invoking(() => new GrowthBookClient(options)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AStreamingHostThatIsNotAUrlIsRejectedWhenTheClientIsCreated()
    {
        FluentActions.Invoking(() => new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            StreamingHost = "not a url",
            LoggerFactory = NullLoggerFactory.Instance
        })).Should().Throw<ArgumentException>();
    }
}
