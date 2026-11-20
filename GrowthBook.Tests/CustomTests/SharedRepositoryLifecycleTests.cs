using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// GrowthBookFactory deliberately hands the same IGrowthBookFeatureRepository to every per-user instance.
/// A repository's CancellationTokenSource is never recreated, so cancelling it is permanent - which makes it
/// important that disposing one per-user instance doesn't take feature refresh and SSE down for everyone.
/// </summary>
public class SharedRepositoryLifecycleTests : UnitTest
{
    private sealed class CancellationTrackingRepository : IGrowthBookFeatureRepository
    {
        private readonly IDictionary<string, Feature> _features;

        public CancellationTrackingRepository(IDictionary<string, Feature> features) => _features = features;

        private int _cancelCallCount;

        // Interlocked, because one of these tests disposes from several threads at once and a
        // miscounted race would read as the fix working.
        public int CancelCallCount => Volatile.Read(ref _cancelCallCount);

        public void Cancel() => Interlocked.Increment(ref _cancelCallCount);

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) => Task.FromResult(_features);
        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) => Task.FromResult(_features);
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;
    }

    private static IDictionary<string, Feature> CreateFeatures() => new Dictionary<string, Feature>
    {
        ["test-feature"] = new Feature { DefaultValue = true }
    };

    [Fact]
    public void DisposingAnInstanceDoesNotCancelARepositoryItDoesNotOwn()
    {
        var repository = new CancellationTrackingRepository(CreateFeatures());

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = repository
        };

        var growthBook = new GrowthBook(context);
        growthBook.Dispose();

        repository.CancelCallCount.Should().Be(0, "because the repository came in through the Context, so this instance doesn't own its lifetime");
    }

    /// <summary>
    /// The two halves of teardown have to hold at once: the destroy callbacks run, and the shared
    /// repository survives. Running the callbacks is not a reason to go back to cancelling it, and
    /// not cancelling it is not a reason to stop short of running them.
    /// </summary>
    [Fact]
    public void AnInstanceRunsItsDestroyCallbacksWithoutCancellingARepositoryItShares()
    {
        var repository = new CancellationTrackingRepository(CreateFeatures());

        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = repository
        });

        var ran = false;

        growthBook.OnDestroy(() => ran = true);

        growthBook.Dispose();

        ran.Should().BeTrue("the callback belongs to this instance, which really is being torn down");
        repository.CancelCallCount.Should().Be(0, "while the repository belongs to whoever supplied it");
        growthBook.IsDestroyed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposingOnePerUserInstanceLeavesTheSharedRepositoryUsableForOthers()
    {
        const string FeatureName = "test-feature";

        var repository = new CancellationTrackingRepository(CreateFeatures());

        var baseContext = new Context
        {
            ClientKey = "test-key",
            FeatureRepository = repository
        };

        using var factory = new GrowthBookFactory(baseContext);

        var firstUser = factory.CreateForUser(new { id = "user-1" });
        var secondUser = factory.CreateForUser(new { id = "user-2" });

        // Typical request-scoped usage disposes each per-user instance as its request completes.
        firstUser.Dispose();

        repository.CancelCallCount.Should().Be(0, "because one finished request must not stop streaming for every other user");

        // Going through LoadFeatures proves the shared repository is still functional, not just that the
        // second instance happens to hold onto features it already had.
        await secondUser.LoadFeatures();

        secondUser.IsOn(FeatureName).Should().BeTrue("because the second user can still load features from the shared repository");
        secondUser.Dispose();
    }

    [Fact]
    public void DisposingTheFactoryCancelsTheSharedRepository()
    {
        var repository = new CancellationTrackingRepository(CreateFeatures());

        var baseContext = new Context
        {
            ClientKey = "test-key",
            FeatureRepository = repository
        };

        var factory = new GrowthBookFactory(baseContext);
        using (factory.CreateForUser(new { id = "user-1" }))
        {
        }

        repository.CancelCallCount.Should().Be(0);

        factory.Dispose();

        repository.CancelCallCount.Should().Be(1, "because the factory owns the shared repository's lifetime");
    }

    [Fact]
    public void DisposingTheFactoryTwiceOnlyCancelsOnce()
    {
        var repository = new CancellationTrackingRepository(CreateFeatures());
        var factory = new GrowthBookFactory(new Context { ClientKey = "test-key", FeatureRepository = repository });

        factory.Dispose();
        factory.Dispose();

        repository.CancelCallCount.Should().Be(1);
    }

    /// <summary>
    /// Two callers disposing at once must still cancel once. Checking the flag and then setting it
    /// lets both get past the check, and a supplied repository is under no obligation to tolerate
    /// being cancelled twice.
    /// </summary>
    [Fact]
    public void DisposingTheFactoryFromSeveralThreadsAtOnceStillCancelsOnce()
    {
        const int Disposers = 32;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var repository = new CancellationTrackingRepository(CreateFeatures());
            var factory = new GrowthBookFactory(new Context { ClientKey = "test-key", FeatureRepository = repository });

            using var start = new ManualResetEventSlim(false);

            var disposers = new Task[Disposers];

            for (var i = 0; i < Disposers; i++)
            {
                disposers[i] = Task.Run(() =>
                {
                    start.Wait(TimeSpan.FromSeconds(10));
                    factory.Dispose();
                });
            }

            start.Set();
            Task.WaitAll(disposers);

            repository.CancelCallCount.Should().Be(1,
                $"the shared repository must be cancelled exactly once however many callers race (attempt {attempt})");
        }
    }

    /// <summary>
    /// Reads the token the instance-created refresh worker runs under. The repository is built
    /// internally so a test has no handle on it, and asserting only that Dispose does not throw
    /// would hold just as well if the owned-repository cancellation were removed entirely.
    /// </summary>
    private static bool OwnedRefreshWorkerIsCancelled(GrowthBook growthBook)
    {
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        var repository = typeof(GrowthBook)
            .GetField("_featureRepository", Private)
            .GetValue(growthBook);

        var worker = repository.GetType()
            .GetField("_backgroundRefreshWorker", Private)
            .GetValue(repository);

        var cancellation = (CancellationTokenSource)worker.GetType()
            .GetField("_refreshWorkerCancellation", Private)
            .GetValue(worker);

        return cancellation.IsCancellationRequested;
    }

    [Fact]
    public void DisposingAnInstanceStillCancelsARepositoryItCreatedItself()
    {
        // No FeatureRepository on the Context, so the instance builds its own and has to clean it up -
        // otherwise its background refresh worker would outlive it.
        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = CreateFeatures()
        };

        var growthBook = new GrowthBook(context);

        OwnedRefreshWorkerIsCancelled(growthBook).Should().BeFalse("the worker is running until the instance is disposed");

        growthBook.Dispose();

        OwnedRefreshWorkerIsCancelled(growthBook).Should().BeTrue(
            "an instance that built its own repository owns it, so nothing else will ever stop its background worker");
    }
}
