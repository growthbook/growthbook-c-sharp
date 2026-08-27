using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers refreshed features being pushed into live instances rather than only into the cache.
/// The reference SDK does this in <c>onNewFeatureData</c>, which hands each new payload to every
/// subscribed instance via <c>setPayload</c>; without it a long-lived instance keeps evaluating the
/// snapshot it was constructed with, so features arriving over a streaming connection never reach the
/// synchronous accessors at all.
/// </summary>
public class PushRefreshedFeaturesTests
{
    private static Feature FlagWith(bool value) => new Feature { DefaultValue = value };

    private static Dictionary<string, Feature> FeatureSet(bool value) =>
        new Dictionary<string, Feature> { ["flag"] = FlagWith(value) };

    private static FeatureRepository CreateRepository(out InMemoryFeatureCache cache)
    {
        cache = new InMemoryFeatureCache(cacheExpirationInSeconds: 60);

        return new FeatureRepository(
            NullLogger<FeatureRepository>.Instance,
            cache,
            Substitute.For<IGrowthBookFeatureRefreshWorker>());
    }

    [Fact]
    public async Task ASynchronousEvaluationSeesFeaturesThatArrivedAfterConstruction()
    {
        var repository = CreateRepository(out var cache);

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        });

        growthBook.IsOn("flag").Should().BeFalse("because that is the snapshot it was constructed with");

        // Exactly what the streaming path does: write the new definitions into the cache.
        await cache.RefreshWith(FeatureSet(true));

        growthBook.IsOn("flag").Should().BeTrue(
            "because a refresh has to reach the instance - the synchronous accessors never reload, so a cache-only update would be invisible to them forever");
    }

    [Fact]
    public async Task EveryInstanceSharingARepositorySeesTheRefresh()
    {
        var repository = CreateRepository(out var cache);

        var baseContext = new Context
        {
            Attributes = new JObject(),
            Features = FeatureSet(false),
            FeatureRepository = repository
        };

        using var factory = new GrowthBookFactory(baseContext);

        var first = factory.CreateForUser(new { id = "user-1" });
        var second = factory.CreateForUser(new { id = "user-2" });

        await cache.RefreshWith(FeatureSet(true));

        first.IsOn("flag").Should().BeTrue();
        second.IsOn("flag").Should().BeTrue("because the reference SDK pushes to every subscribed instance, not just the first");
    }

    [Fact]
    public async Task ARefreshDoesNotDisturbPerInstanceState()
    {
        var repository = CreateRepository(out var cache);

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1", country = "UA" }),
            Features = FeatureSet(false),
            ForcedVariations = new Dictionary<string, int> { ["some-experiment"] = 1 },
            FeatureRepository = repository
        });

        await cache.RefreshWith(FeatureSet(true));

        growthBook.Attributes["country"].Value<string>().Should().Be("UA", "because attributes belong to the instance, not to the payload");
        growthBook.ForcedVariations.Should().ContainKey("some-experiment");
    }

    /// <summary>
    /// Counts the handlers a source is still holding. Asserting on behavior alone cannot distinguish a
    /// disposed instance that unsubscribed from one that stayed registered and merely ignores the callback -
    /// and the difference is exactly the leak: a repository that lives for the process keeps every instance
    /// it ever pushed to alive.
    /// </summary>
    private static int HandlerCount(object source)
    {
        var subscriptions = source.GetType()
            .GetField("_refreshSubscriptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(source);

        var handlers = subscriptions.GetType()
            .GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(subscriptions);

        return ((System.Collections.ICollection)handlers).Count;
    }

    [Fact]
    public async Task DisposingAnInstanceReleasesItFromTheRepository()
    {
        var repository = CreateRepository(out var cache);

        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        });

        HandlerCount(repository).Should().Be(1, "because the instance subscribed at construction");

        growthBook.Dispose();

        HandlerCount(repository).Should().Be(0,
            "because a repository that outlives its instances would otherwise hold every one of them alive for the life of the process");

        Func<Task> refresh = () => cache.RefreshWith(FeatureSet(true));

        await refresh.Should().NotThrowAsync();
    }

    [Fact]
    public void DisposingOneOfSeveralInstancesLeavesTheOthersSubscribed()
    {
        var repository = CreateRepository(out _);

        var baseContext = new Context
        {
            Attributes = new JObject(),
            Features = FeatureSet(false),
            FeatureRepository = repository
        };

        using var factory = new GrowthBookFactory(baseContext);

        var first = factory.CreateForUser(new { id = "user-1" });
        using var second = factory.CreateForUser(new { id = "user-2" });

        HandlerCount(repository).Should().Be(2);

        first.Dispose();

        HandlerCount(repository).Should().Be(1, "because disposing one scoped instance must not detach the others");
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotStopTheOthers()
    {
        var cache = new InMemoryFeatureCache(cacheExpirationInSeconds: 60);
        var repository = new FeatureRepository(
            NullLogger<FeatureRepository>.Instance,
            cache,
            Substitute.For<IGrowthBookFeatureRefreshWorker>());

        var reachedSecond = false;

        using (((IFeatureRefreshSource)repository).SubscribeToRefresh(_ => throw new InvalidOperationException("subscriber blew up")))
        using (((IFeatureRefreshSource)repository).SubscribeToRefresh(_ => reachedSecond = true))
        {
            Func<Task> refresh = () => cache.RefreshWith(FeatureSet(true));

            await refresh.Should().NotThrowAsync("because a refresh must not fail on account of a subscriber");
        }

        reachedSecond.Should().BeTrue("because one bad subscriber cannot stop the rest from seeing the refresh");
    }

    [Fact]
    public async Task DisposingASubscriptionStopsThatHandlerOnly()
    {
        var cache = new InMemoryFeatureCache(cacheExpirationInSeconds: 60);
        var repository = new FeatureRepository(
            NullLogger<FeatureRepository>.Instance,
            cache,
            Substitute.For<IGrowthBookFeatureRefreshWorker>());

        var cancelledCount = 0;
        var remainingCount = 0;

        var cancelled = ((IFeatureRefreshSource)repository).SubscribeToRefresh(_ => cancelledCount++);
        using var remaining = ((IFeatureRefreshSource)repository).SubscribeToRefresh(_ => remainingCount++);

        await cache.RefreshWith(FeatureSet(true));

        cancelledCount.Should().Be(1);
        remainingCount.Should().Be(1);

        cancelled.Dispose();

        await cache.RefreshWith(FeatureSet(false));

        cancelledCount.Should().Be(1, "because the disposed subscription must not fire again");
        remainingCount.Should().Be(2);
    }

    [Fact]
    public void ARepositoryThatDoesNotSupportPushingStillWorks()
    {
        // A custom IGrowthBookFeatureRepository that predates IFeatureRefreshSource simply never pushes,
        // which is the behavior it has today. It must not fail to construct.
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(true),
            FeatureRepository = repository
        });

        growthBook.IsOn("flag").Should().BeTrue();
    }

    [Fact]
    public async Task TheCacheStillServesTheRefreshedFeaturesItself()
    {
        var repository = CreateRepository(out var cache);

        await cache.RefreshWith(FeatureSet(true));

        var served = await cache.GetFeatures();

        served.Should().ContainKey("flag");
        served["flag"].DefaultValue.Value<bool>().Should().BeTrue("because pushing to subscribers must not replace the cache's own job");
    }
}
