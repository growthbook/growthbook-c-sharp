using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Api;
using GrowthBook.Exceptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests
{
    public class FeatureRepositoryRemoteEvaluationTests
    {
        [Fact]
        public async Task GetFeaturesWithContext_ShouldHandleRemoteEvaluation()
        {
            var logger = Substitute.For<ILogger<FeatureRepository>>();
            var cache = Substitute.For<IGrowthBookFeatureCache>();
            var worker = Substitute.For<IGrowthBookFeatureRefreshWorker>();
            var remoteService = Substitute.For<IRemoteEvaluationService>();
            var repository = new FeatureRepository(logger, cache, worker, remoteService);

            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            // Test remote eval disabled - should use cache
            cache.IsCacheExpired.Returns(false);
            cache.GetFeatures(Arg.Any<System.Threading.CancellationToken?>()).Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            var disabledContext = new Context { RemoteEval = false };
            var result1 = await repository.GetFeaturesWithContext(disabledContext);
            result1.Should().BeSameAs(features);

            // Test remote eval enabled - should use remote service
            var enabledContext = new Context { RemoteEval = true, ClientKey = "key", ApiHost = "https://api.example.com" };
            var successResponse = RemoteEvaluationResponse.CreateSuccess(features);
            remoteService.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RemoteEvaluationRequest>(),
                Arg.Any<IDictionary<string, string>>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(successResponse));

            var result2 = await repository.GetFeaturesWithContext(enabledContext);
            result2.Should().BeSameAs(features);

            // Test remote eval failure - should throw
            var errorResponse = RemoteEvaluationResponse.CreateError(HttpStatusCode.BadRequest, "Error");
            remoteService.EvaluateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RemoteEvaluationRequest>(),
                Arg.Any<IDictionary<string, string>>(), Arg.Any<System.Threading.CancellationToken>())
                .Returns(Task.FromResult(errorResponse));

            await Assert.ThrowsAsync<RemoteEvaluationException>(() => repository.GetFeaturesWithContext(enabledContext));
        }
    }
}
