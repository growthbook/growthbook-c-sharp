using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

/// <summary>
/// A remote evaluation request is built from the user's attributes, and its response can carry saved groups, which
/// are typically lists of user identifiers. Neither may reach the logs: a debug log ends up in whatever sink the host
/// has configured, with that sink's retention and access.
/// </summary>
public class RemoteEvaluationLoggingTests
{
    private const string AttributeValue = "synthetic-user-7f3a";
    private const string ResponseValue = "synthetic-group-member-91c2";

    [Fact]
    public async Task NeitherTheAttributesNorTheResponseAreLogged()
    {
        var logger = new CapturingLogger();
        var response = $@"{{""features"": {{""flag"": {{""defaultValue"": true}}}}, ""savedGroups"": {{""grp"": [""{ResponseValue}""]}}}}";
        var service = new RemoteEvaluationService(logger, Factory(HttpStatusCode.OK, response));

        var result = await service.EvaluateAsync("https://api.example.test", "sdk-test", new RemoteEvaluationRequest
        {
            Attributes = new JObject { ["id"] = AttributeValue },
            ForcedFeatures = new List<List<object>> { new List<object> { "flag", AttributeValue } }
        });

        result.IsSuccess.Should().BeTrue();
        logger.Messages.Should().NotBeEmpty("debug logging is on, so the request and response are still described");
        logger.Messages.Should().NotContain(m => m.Contains(AttributeValue), "the request is built from the user's attributes");
        logger.Messages.Should().NotContain(m => m.Contains(ResponseValue), "the response can carry lists of user identifiers");
        logger.Messages.Should().Contain(m => m.Contains("1 attributes"), "the shape of the request is still logged");
    }

    private static IHttpClientFactory Factory(HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new FixedHandler(status, body)));

        return factory;
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FixedHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
    }

    private sealed class CapturingLogger : ILogger<RemoteEvaluationService>
    {
        public List<string> Messages { get; } = new List<string>();

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
