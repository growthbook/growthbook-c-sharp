using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Api;
using GrowthBook.Exceptions;
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

        [Fact]
        public async Task EvaluateAsync_ShouldRetryATransientFailureAndSucceed()
        {
            var attempts = 0;

            var httpClient = CreateHttpClient((request, cancellationToken) =>
            {
                attempts++;

                if (attempts < 3)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SuccessResponseJson(), Encoding.UTF8, "application/json")
                });
            });

            var service = CreateService(httpClient, NoWaitPolicy(maxAttempts: 3));

            var result = await service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest());

            attempts.Should().Be(3);
            result.IsSuccess.Should().BeTrue();
            result.Features.Should().ContainKey("test");
        }

        [Fact]
        public async Task EvaluateAsync_ShouldStopAtTheAttemptLimitAndReportTheLastFailure()
        {
            var attempts = 0;

            var httpClient = CreateHttpClient((request, cancellationToken) =>
            {
                attempts++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream is down", Encoding.UTF8, "application/json")
                });
            });

            var service = CreateService(httpClient, NoWaitPolicy(maxAttempts: 3));

            var result = await service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest());

            attempts.Should().Be(3);
            result.IsSuccess.Should().BeFalse();
            result.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }

        [Fact]
        public async Task EvaluateAsync_ShouldNotRetryAStatusThatAnotherAttemptCannotFix()
        {
            var attempts = 0;

            var httpClient = CreateHttpClient((request, cancellationToken) =>
            {
                attempts++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("malformed payload", Encoding.UTF8, "application/json")
                });
            });

            var service = CreateService(httpClient, NoWaitPolicy(maxAttempts: 3));

            var result = await service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest());

            // Repeating a rejected request would only produce the same rejection
            attempts.Should().Be(1);
            result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task EvaluateAsync_ShouldRetryATransportErrorAndThrowWhenAttemptsRunOut()
        {
            var attempts = 0;

            var httpClient = CreateHttpClient((request, cancellationToken) =>
            {
                attempts++;

                throw new HttpRequestException("connection reset");
            });

            var service = CreateService(httpClient, NoWaitPolicy(maxAttempts: 2));

            await Assert.ThrowsAsync<RemoteEvaluationException>(
                () => service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest()));

            attempts.Should().Be(2);
        }

        [Fact]
        public async Task EvaluateAsync_ShouldSendTheSamePayloadOnEveryAttempt()
        {
            var bodies = new List<string>();

            var httpClient = CreateHttpClient(async (request, cancellationToken) =>
            {
                bodies.Add(await request.Content.ReadAsStringAsync());

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
                };
            });

            var service = CreateService(httpClient, NoWaitPolicy(maxAttempts: 3));
            var evaluationRequest = new RemoteEvaluationRequest { Attributes = new JObject { ["id"] = "user_123" } };

            await service.EvaluateAsync("https://api.example.com", "clientKey", evaluationRequest);

            // A retry must not ship state the caller changed in the meantime, so the payload is fixed up front
            bodies.Should().HaveCount(3);
            bodies.Distinct().Should().HaveCount(1);
            bodies[0].Should().Contain("user_123");
        }

        [Fact]
        public void GetRetryDelay_ShouldBackOffExponentiallyAndRespectTheCap()
        {
            var policy = new RemoteEvaluationRetryPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                JitterRatio = 0
            };

            policy.GetRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(500));
            policy.GetRetryDelay(2).Should().Be(TimeSpan.FromSeconds(1));
            policy.GetRetryDelay(3).Should().Be(TimeSpan.FromSeconds(2));

            // Doubling would reach 8s, which would keep an awaiting caller waiting longer than the cap allows
            policy.GetRetryDelay(5).Should().Be(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void GetRetryDelay_ShouldPreferTheServersRetryAfterWithinTheCap()
        {
            var policy = new RemoteEvaluationRetryPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                JitterRatio = 0
            };

            policy.GetRetryDelay(1, TimeSpan.FromSeconds(2)).Should().Be(TimeSpan.FromSeconds(2));
            policy.GetRetryDelay(1, TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task EvaluateAsync_ShouldStopRetryingOnceTheTimeBudgetIsSpent()
        {
            var attempts = 0;

            var httpClient = CreateHttpClient((request, cancellationToken) =>
            {
                attempts++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
                });
            });

            // Three attempts are allowed, but the backoff before the second one is longer than the whole round is
            // allowed to take. The wait is clamped to what's left of the budget, and then there is nothing left.
            // Deliberately spending the budget between attempts rather than during one: a budget small enough to
            // expire mid-request makes the first attempt's outcome a race against its own cancellation.
            var service = CreateService(httpClient, new RemoteEvaluationRetryPolicy
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
                MaxTotalDuration = TimeSpan.FromMilliseconds(100)
            });

            var result = await service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest());

            attempts.Should().Be(1);
            result.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        [Fact]
        public async Task EvaluateAsync_ShouldNotLetASlowAttemptRunPastTheTimeBudget()
        {
            var httpClient = CreateHttpClient(async (request, cancellationToken) =>
            {
                // A server that accepts the connection and then never answers
                await Task.Delay(Timeout.Infinite, cancellationToken);

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            var service = CreateService(httpClient, new RemoteEvaluationRetryPolicy
            {
                MaxAttempts = 3,
                MaxDelay = TimeSpan.Zero,
                MaxTotalDuration = TimeSpan.FromMilliseconds(200)
            });

            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAsync<RemoteEvaluationException>(
                () => service.EvaluateAsync("https://api.example.com", "clientKey", new RemoteEvaluationRequest()));

            stopwatch.Stop();

            // Without the budget this would hang until the HTTP timeout, three times over
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }

        private static RemoteEvaluationRetryPolicy NoWaitPolicy(int maxAttempts) =>
            new RemoteEvaluationRetryPolicy
            {
                MaxAttempts = maxAttempts,
                MaxDelay = TimeSpan.Zero,
                // Unlimited, so a slow machine can't cut a run short and make these tests flaky
                MaxTotalDuration = TimeSpan.Zero
            };

        private static RemoteEvaluationService CreateService(HttpClient httpClient, RemoteEvaluationRetryPolicy retryPolicy)
        {
            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            httpClientFactory.CreateClient(Arg.Is(ConfiguredClients.DefaultApiClient)).Returns(httpClient);

            return new RemoteEvaluationService(Substitute.For<ILogger<RemoteEvaluationService>>(), httpClientFactory, retryPolicy);
        }

        private static string SuccessResponseJson() =>
            JsonConvert.SerializeObject(new RemoteEvaluationResponse
            {
                Features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } },
                DateUpdated = DateTimeOffset.UtcNow
            });

        private HttpClient CreateHttpClient(Func<HttpRequestMessage, System.Threading.CancellationToken, Task<HttpResponseMessage>> handler) =>
            new HttpClient(new TestHttpMessageHandler(handler));

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
