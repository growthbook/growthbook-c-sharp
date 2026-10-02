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
    [Fact]
    public async Task DisposingOneInstanceLeavesTheFeaturesOfAnotherIntact()
    {
        var repository = CreateRepository(out var cache);

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        };

        using var survivor = new GrowthBook(context.Clone());
        var doomed = new GrowthBook(context.Clone());

        await cache.RefreshWith(FeatureSet(true));

        survivor.IsOn("flag").Should().BeTrue();
        doomed.IsOn("flag").Should().BeTrue();

        doomed.Dispose();

        survivor.Features.Should().ContainKey("flag",
            "because Dispose() clears the disposing instance's own features - every subscriber is handed the same dictionary, so adopting that reference would let one instance empty the map for all the others");
        survivor.IsOn("flag").Should().BeTrue();
    }

    [Fact]
    public void DisposingARepositoryDetachesItFromTheCache()
    {
        var repository = CreateRepository(out var cache);

        HandlerCount(cache).Should().Be(1, "because the repository subscribed to the cache at construction");

        repository.Dispose();

        HandlerCount(cache).Should().Be(0,
            "because a cache supplied on the Context outlives the repository - staying subscribed would keep fanning refreshes out to dead subscribers");
    }

    [Fact]
    public void DisposingAnInstanceDetachesTheRepositoryItOwnsFromItsCache()
    {
        var callerOwned = CreateRepository(out var callerOwnedCache);

        using (new GrowthBook(new Context { Features = FeatureSet(false), FeatureRepository = callerOwned }))
        {
        }

        HandlerCount(callerOwnedCache).Should().Be(1,
            "because a repository supplied on the Context belongs to the caller and may be shared, so disposing one instance must not detach it");

        callerOwned.Dispose();
    }

    /// <summary>
    /// An API refresh and a streaming refresh can be in flight at once. The cache writes under a lock
    /// but fans out without one, so the two notifications can reach an instance in the opposite order
    /// to the writes. Arriving late, the older one would roll the instance back to definitions the
    /// cache has already replaced, and nothing would correct it until the next refresh.
    /// </summary>
    [Fact]
    public async Task ARefreshOvertakenOnItsWayToAnInstanceDoesNotRollItBack()
    {
        var repository = CreateRepository(out var cache);

        var parkFirstFanOut = new ManualResetEventSlim(false);
        var fanOutParked = new SemaphoreSlim(0);
        var remainingParks = 1;

        // Registered before the instance, so parking here holds the first refresh between the cache
        // write and the instance seeing it - the window the fix has to close.
        using var park = ((IFeatureRefreshSource)repository).SubscribeToRefresh(_ =>
        {
            if (Interlocked.Exchange(ref remainingParks, 0) == 1)
            {
                fanOutParked.Release();
                parkFirstFanOut.Wait(TimeSpan.FromSeconds(5));
            }
        });

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        });

        var overtaken = Task.Run(() => cache.RefreshWith(FeatureSet(false)));
        fanOutParked.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("because the first refresh has to be in flight");

        await cache.RefreshWith(FeatureSet(true));
        growthBook.IsOn("flag").Should().BeTrue("because the second refresh reached the instance");

        parkFirstFanOut.Set();
        await overtaken;

        growthBook.IsOn("flag").Should().BeTrue(
            "because the refresh that lost the race is stale by the time it arrives and must be dropped, not installed");
    }

    /// <summary>
    /// A repository whose fetch finishes only when the test says so, and which can push a refresh in
    /// the meantime, so the order of the two is the test's to decide rather than the scheduler's.
    /// </summary>
    private sealed class ControllableRepository : IGrowthBookFeatureRepository, IFeatureRefreshSource
    {
        private readonly List<Action<FeatureRefresh>> _handlers = new List<Action<FeatureRefresh>>();
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly SemaphoreSlim _entered = new SemaphoreSlim(0);

        public IDictionary<string, Feature> Result { get; set; }

        public bool WaitUntilFetching() => _entered.Wait(TimeSpan.FromSeconds(5));
        public void FinishFetch() => _release.Set();

        public void Push(long version, IDictionary<string, Feature> features)
        {
            Action<FeatureRefresh>[] handlers;

            lock (_handlers)
            {
                handlers = _handlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(new FeatureRefresh(version, features));
            }
        }

        public IDisposable SubscribeToRefresh(Action<FeatureRefresh> handler)
        {
            lock (_handlers)
            {
                _handlers.Add(handler);
            }

            return new Unsubscriber(this, handler);
        }

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(5));

            return Task.FromResult(Result);
        }

        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) =>
            GetFeatures(options, cancellationToken);

        public void Cancel() { }
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ControllableRepository _owner;
            private readonly Action<FeatureRefresh> _handler;

            public Unsubscriber(ControllableRepository owner, Action<FeatureRefresh> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public void Dispose()
            {
                lock (_owner._handlers)
                {
                    _owner._handlers.Remove(_handler);
                }
            }
        }
    }

    /// <summary>
    /// A load that was already waiting on the API when a refresh arrived is holding definitions older
    /// than the ones now installed. Writing its result on completion would undo the refresh.
    /// </summary>
    [Fact]
    public async Task ALoadInFlightDoesNotOverwriteARefreshThatArrivedWhileItWaited()
    {
        var repository = new ControllableRepository { Result = FeatureSet(false) };

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        });

        var load = Task.Run(() => growthBook.LoadFeaturesWithResult());
        repository.WaitUntilFetching().Should().BeTrue("because the load has to be waiting on the repository");

        repository.Push(1, FeatureSet(true));
        growthBook.IsOn("flag").Should().BeTrue("because the refresh reached the instance while the load waited");

        repository.FinishFetch();
        var result = await load;

        result.Success.Should().BeTrue("because the load itself did succeed");
        growthBook.IsOn("flag").Should().BeTrue(
            "because the load's result was already stale when it arrived and must not replace the newer definitions");
    }

    [Fact]
    public async Task ALoadStillInstallsItsResultWhenNoRefreshArrived()
    {
        var repository = new ControllableRepository { Result = FeatureSet(true) };

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = FeatureSet(false),
            FeatureRepository = repository
        });

        repository.FinishFetch();

        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeTrue();
        growthBook.IsOn("flag").Should().BeTrue("because nothing newer landed, so the load's result is the current one");
    }
}
