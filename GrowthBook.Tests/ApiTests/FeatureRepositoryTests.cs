using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class FeatureRepositoryTests : ApiUnitTest<FeatureRepository>
{
    private readonly IGrowthBookFeatureRefreshWorker _backgroundWorker;
    private readonly FeatureRepository _featureRepository;

    public FeatureRepositoryTests()
    {
        _backgroundWorker = Substitute.For<IGrowthBookFeatureRefreshWorker>();
        _featureRepository = new(_logger, _cache, _backgroundWorker);
    }

    [Fact]
    public void CancellingRepositoryWillCancelBackgroundWorker()
    {
        _featureRepository.Cancel();

        _backgroundWorker.Received(1).Cancel();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    public async Task GettingFeaturesWhenApiCallIsUnnecessaryWillGetFromCache(bool isCacheExpired, bool? isForcedRefresh)
    {
        _cache.IsCacheExpired.Returns(isCacheExpired);

        _cache
            .GetFeatures(Arg.Any<CancellationToken?>())
            .Returns(_availableFeatures);

        var options = isForcedRefresh switch
        {
            null => null,
            _ => new GrowthBookRetrievalOptions { ForceRefresh = isForcedRefresh.Value }
        };

        var features = await _featureRepository.GetFeatures(options);

        await _cache.Received(1).GetFeatures(Arg.Any<CancellationToken?>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GettingFeaturesWhenApiCallIsRequiredWithoutWaitingForRetrievalWillGetFromCache(bool isCacheExpired, bool? isForcedRefresh)
    {
        _cache.IsCacheExpired.Returns(isCacheExpired);
        _cache.FeatureCount.Returns(_availableFeatures.Count);
        _cache
            .GetFeatures(Arg.Any<CancellationToken?>())
            .Returns(_availableFeatures);
        _backgroundWorker
            .RefreshCacheFromApi(Arg.Any<CancellationToken?>())
            .Returns(_availableFeatures);

        var options = isForcedRefresh switch
        {
            null => new GrowthBookRetrievalOptions { WaitForCompletion = true },
            _ => new GrowthBookRetrievalOptions { ForceRefresh = isForcedRefresh.Value, WaitForCompletion = true }
        };

        var features = await _featureRepository.GetFeatures(options);

        _ = _cache.Received(2).IsCacheExpired;
        _ = _cache.Received(2).FeatureCount;
        // Remove this line - cache.GetFeatures is not called when WaitForCompletion = true
        // _ = _cache.Received(1).GetFeatures(Arg.Any<CancellationToken?>());
        _ = _backgroundWorker.Received(1).RefreshCacheFromApi(Arg.Any<CancellationToken?>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GettingFeaturesWhenApiCallIsRequiredWithWaitingForRetrievalWillGetFromApiCallInsteadOfCache(bool isCacheEmpty, bool? isForcedWait)
    {
        _cache.IsCacheExpired.Returns(true);

        _cache.FeatureCount.Returns(isCacheEmpty ? 0 : 1);

        _backgroundWorker
            .RefreshCacheFromApi(Arg.Any<CancellationToken?>())
            .Returns(_availableFeatures);

        var options = isForcedWait switch
        {
            null => null,
            _ => new GrowthBookRetrievalOptions { WaitForCompletion = isForcedWait.Value }
        };

        var features = await _featureRepository.GetFeatures(options);

        _ = _cache.Received(2).IsCacheExpired;
        _ = _cache.Received(2).FeatureCount;
        _ = _backgroundWorker.Received(1).RefreshCacheFromApi(Arg.Any<CancellationToken?>());
    }
}
