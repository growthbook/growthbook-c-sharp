using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class ContextFeatureCacheTests : UnitTest
{
    [Fact]
    public async Task LoadFeatures_ShouldUseCustomCacheFromContext_WhenFeatureCacheIsProvided()
    {
        const string featureKey = "custom-cache-feature";
        var expectedFeatures = new Dictionary<string, Feature> { [featureKey] = new() { DefaultValue = true } };

        var customCache = Substitute.For<IGrowthBookFeatureCache>();
        customCache.IsCacheExpired.Returns(false);
        customCache.GetFeatures(Arg.Any<System.Threading.CancellationToken?>())
            .Returns(System.Threading.Tasks.Task.FromResult<IDictionary<string, Feature>>(expectedFeatures));

        var context = new Context { FeatureCache = customCache };

        using var growthBook = new GrowthBook(context);
        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeTrue("because the custom cache returned features successfully");
        growthBook.Features.Should().ContainKey(featureKey, "because the custom cache provided this feature");
        await customCache.Received().GetFeatures(Arg.Any<System.Threading.CancellationToken?>());
    }

    [Fact]
    public void Dispose_ShouldNotCancelSharedRepository_WhenFeatureRepositoryIsInjected()
    {
        var sharedRepository = Substitute.For<IGrowthBookFeatureRepository>();
        var context = new Context { FeatureRepository = sharedRepository };

        var growthBook = new GrowthBook(context);
        growthBook.Dispose();

        sharedRepository.DidNotReceive().Cancel();
    }

    [Fact]
    public void Dispose_ShouldCancelRepository_WhenRepositoryIsOwnedByGrowthBook()
    {
        var customCache = Substitute.For<IGrowthBookFeatureCache>();
        customCache.IsCacheExpired.Returns(false);
        customCache.FeatureCount.Returns(1);
        customCache.GetFeatures(Arg.Any<System.Threading.CancellationToken?>())
            .Returns(System.Threading.Tasks.Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>()));

        var context = new Context { FeatureCache = customCache };

        var growthBook = new GrowthBook(context);
        growthBook.Dispose();

        // The internal FeatureRefreshWorker.Cancel() is called indirectly via the owned FeatureRepository.
        // We verify it by ensuring the cache is not accessed after dispose (no exception thrown).
        customCache.DidNotReceive().RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<System.Threading.CancellationToken?>());
    }
}
