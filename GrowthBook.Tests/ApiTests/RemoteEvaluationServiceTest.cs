using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests
{
    public class RemoteEvaluationServiceTests
    {
        [Fact]
        public async Task EvaluateAsync_ShouldWork()
        {
            var logger = Substitute.For<ILogger<RemoteEvaluationService>>();
            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            var service = new RemoteEvaluationService(logger, httpClientFactory);

        
            var features = new Dictionary<string, Feature>
            {
                { "test", new Feature { DefaultValue = true } }
            };

            var apiResponse = new RemoteEvaluationResponse
            {
                Features = features,
                DateUpdated = DateTimeOffset.UtcNow
            };

            var responseJson = JsonConvert.SerializeObject(apiResponse);

            var httpClient = CreateHttpClientWithResponse(HttpStatusCode.OK, responseJson);
            httpClientFactory.CreateClient(Arg.Is(ConfiguredClients.DefaultApiClient)).Returns(httpClient);

            var request = new RemoteEvaluationRequest
            {
                Attributes = new JObject { ["id"] = "user_123" },
                 Url = "https://api.example.com"
            };
            var result = await service.EvaluateAsync("https://api.example.com", "clientKey", request);

            result.IsSuccess.Should().BeTrue();
            result.Features.Should().ContainKey("test");
        }

        [Fact]
        public void GetRemoteEvaluationUrl_ShouldGenerateCorrectUrl()
        {
            var logger = Substitute.For<ILogger<RemoteEvaluationService>>();
            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            var service = new RemoteEvaluationService(logger, httpClientFactory);

            var result = service.GetRemoteEvaluationUrl("https://api.example.com/", "clientKey");
            result.Should().Be("https://api.example.com/api/eval/clientKey");
        }

        [Fact]
        public void ValidateRemoteEvaluationConfiguration_ShouldValidate()
        {
            var service = new RemoteEvaluationService(Substitute.For<ILogger<RemoteEvaluationService>>(), Substitute.For<IHttpClientFactory>());

            // Valid config should not throw
            var validContext = new Context { RemoteEval = true, ClientKey = "key", ApiHost = "https://api.example.com" };
            service.ValidateRemoteEvaluationConfiguration(validContext);

            // Invalid config should throw
            var invalidContext = new Context { RemoteEval = true, ClientKey = "key", DecryptionKey = "key", ApiHost = "https://api.example.com" };
            Assert.Throws<ArgumentException>(() => service.ValidateRemoteEvaluationConfiguration(invalidContext));
        }

        private HttpClient CreateHttpClientWithResponse(HttpStatusCode statusCode, string content)
        {
            var handler = new TestHttpMessageHandler((request, cancellationToken) =>
            {
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            });
            return new HttpClient(handler);
        }

        private class TestHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> _handler;

            public TestHttpMessageHandler(Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return _handler(request, cancellationToken);
            }
        }
    }
}
