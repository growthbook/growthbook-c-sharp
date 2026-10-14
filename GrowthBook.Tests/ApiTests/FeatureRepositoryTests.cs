using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
    public async Task GettingFeaturesWhenApiCallIsRequiredAndWaitingIsAskedForWillGetFromTheApiCall(bool isCacheExpired, bool? isForcedRefresh)
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
        _ = _cache.Received(1).IsCacheExpired;
        _ = _cache.Received(1).FeatureCount;

        await _backgroundWorker.Received(1).RefreshCacheFromApi(Arg.Any<CancellationToken?>());

        await _cache.DidNotReceive().GetFeatures(Arg.Any<CancellationToken?>());

        features.Should().BeEquivalentTo(_availableFeatures);
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

        _ = _cache.Received(1).IsCacheExpired;
        _ = _cache.Received(1).FeatureCount;

        await _backgroundWorker.Received(1).RefreshCacheFromApi(Arg.Any<CancellationToken?>());

        features.Should().BeEquivalentTo(_availableFeatures);
    }

    [Fact]
    public async Task AFreshCacheIsConsultedExactlyOnceAndTheWorkerIsLeftAlone()
    {
        _cache.IsCacheExpired.Returns(false);
        _cache.FeatureCount.Returns(_availableFeatures.Count);
        _cache
            .GetFeatures(Arg.Any<CancellationToken?>())
            .Returns(_availableFeatures);

        var features = await _featureRepository.GetFeatures();


        _ = _cache.Received(1).IsCacheExpired;
        _ = _cache.DidNotReceive().FeatureCount;

        await _cache.Received(1).GetFeatures(Arg.Any<CancellationToken?>());
        await _backgroundWorker.DidNotReceive().RefreshCacheFromApi(Arg.Any<CancellationToken?>());

        features.Should().BeEquivalentTo(_availableFeatures);
    }

    /// <summary>
    /// The case the renamed test above never covered: a populated but expired cache with
    /// <c>WaitForCompletion</c> left off. The refresh is started and not waited on, so the caller is
    /// served what is already cached rather than being blocked on the API, and the fetched values only
    /// appear on a later call. The cached and fetched sets differ here so that the two cannot be confused.
    /// </summary>
    [Fact]
    public async Task GettingFeaturesWithoutWaitingServesTheCachedOnesWhileTheRefreshIsStillRunning()
    {
        var cached = new Dictionary<string, Feature> { ["cached-flag"] = new Feature { DefaultValue = true } };
        var fetched = new Dictionary<string, Feature> { ["fetched-flag"] = new Feature { DefaultValue = true } };

        var releaseRefresh = new TaskCompletionSource<IDictionary<string, Feature>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _cache.IsCacheExpired.Returns(true);
        _cache.FeatureCount.Returns(cached.Count);
        _cache.GetFeatures(Arg.Any<CancellationToken?>()).Returns(cached);

        _backgroundWorker
            .RefreshCacheFromApi(Arg.Any<CancellationToken?>())
            .Returns(_ =>
            {
                refreshStarted.TrySetResult(true);

                return releaseRefresh.Task;
            });

        // Bounded on purpose. A regression here does not return the wrong features, it never returns at
        // all, because the refresh is deliberately left pending - unbounded, that hangs the run instead of
        // failing it.
        var load = _featureRepository.GetFeatures(new GrowthBookRetrievalOptions { WaitForCompletion = false });

        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(5)));

        finished.Should().Be(load, "a caller that did not ask to wait must not be blocked on the API");

        var features = await load;

        features.Should().BeEquivalentTo(cached,
            "a caller that did not ask to wait is served the cache rather than blocked on the API");

        await _cache.Received(1).GetFeatures(Arg.Any<CancellationToken?>());
        await _backgroundWorker.Received(1).RefreshCacheFromApi(Arg.Any<CancellationToken?>());

        (await Task.WhenAny(refreshStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(refreshStarted.Task, "the refresh still has to have been started, just not awaited");

        releaseRefresh.TrySetResult(fetched);
    }
}
