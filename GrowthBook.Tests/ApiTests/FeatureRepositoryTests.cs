using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class FeatureRepositoryTests : ApiUnitTest<FeatureRepository>
{
    private readonly Mock<IGrowthBookFeatureRefreshWorker> _backgroundWorker;
    private readonly FeatureRepository _featureRepository;

    public FeatureRepositoryTests()
    {
        _backgroundWorker = StrictMockOf<IGrowthBookFeatureRefreshWorker>();
        _featureRepository = new(_logger, _cache.Object, _backgroundWorker.Object);
    }

    [Fact]
    public void CancellingRepositoryWillCancelBackgroundWorker()
    {
        _backgroundWorker
            .Setup(x => x.Cancel())
            .Verifiable();

        _featureRepository.Cancel();

        _backgroundWorker.Verify(x => x.Cancel(), Times.Once, "Cancelling the background worker did not succeed");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    public async Task GettingFeaturesWhenApiCallIsUnnecessaryWillGetFromCache(bool isCacheExpired, bool? isForcedRefresh)
    {
        _cache
            .SetupGet(x => x.IsCacheExpired)
            .Returns(isCacheExpired)
            .Verifiable();

        _cache
            .Setup(x => x.GetFeatures(It.IsAny<CancellationToken?>()))
            .ReturnsAsync(_availableFeatures)
            .Verifiable();

        var options = isForcedRefresh switch
        {
            null => null,
            _ => new GrowthBookRetrievalOptions { ForceRefresh = isForcedRefresh.Value }
        };

        var features = await _featureRepository.GetFeatures(options);

        Mock.Verify(_cache);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GettingFeaturesWhenApiCallIsRequiredWithoutWaitingForRetrievalWillGetFromCache(bool isCacheExpired, bool? isForcedRefresh)
    {
        _cache
            .SetupGet(x => x.IsCacheExpired)
            .Returns(isCacheExpired)
            .Verifiable();

        _cache
            .SetupGet(x => x.FeatureCount)
            .Returns(_availableFeatures.Count)
            .Verifiable();

        _cache
            .Setup(x => x.GetFeatures(It.IsAny<CancellationToken?>()))
            .ReturnsAsync(_availableFeatures)
            .Verifiable();

        _backgroundWorker
            .Setup(x => x.RefreshCacheFromApi(It.IsAny<CancellationToken?>()))
            .ReturnsAsync(_availableFeatures)
            .Verifiable();

        var options = isForcedRefresh switch
        {
            null => null,
            _ => new GrowthBookRetrievalOptions { ForceRefresh = isForcedRefresh.Value }
        };

        var features = await _featureRepository.GetFeatures(options);

        Mock.Verify(_cache, _backgroundWorker);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GettingFeaturesWhenApiCallIsRequiredWithWaitingForRetrievalWillGetFromApiCallInsteadOfCache(bool isCacheEmpty, bool? isForcedWait)
    {
        _cache
            .SetupGet(x => x.IsCacheExpired)
            .Returns(true)
            .Verifiable();

        _cache
            .SetupGet(x => x.FeatureCount)
            .Returns(isCacheEmpty ? 0 : 1)
            .Verifiable();

        _backgroundWorker
            .Setup(x => x.RefreshCacheFromApi(It.IsAny<CancellationToken?>()))
            .ReturnsAsync(_availableFeatures)
            .Verifiable();

        var options = isForcedWait switch
        {
            null => null,
            _ => new GrowthBookRetrievalOptions { WaitForCompletion = isForcedWait.Value }
        };

        var features = await _featureRepository.GetFeatures(options);

        Mock.Verify(_cache, _backgroundWorker);
    }
}
