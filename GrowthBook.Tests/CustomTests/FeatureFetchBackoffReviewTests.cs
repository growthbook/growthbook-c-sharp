using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the five defects review found in the fetch backoff. Four of them are about the backoff
/// opening when it should not, which is worse than it failing to open: a healthy API stops being
/// polled and the client serves a stale cache for minutes.
/// </summary>
public class FeatureFetchBackoffReviewTests : UnitTest
{
    private static Dictionary<string, Feature> FeatureSet() =>
        new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } };

    private sealed class ManualClock
    {
        private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private static FeatureRepository CreateRepository(IGrowthBookFeatureRefreshWorker worker, ManualClock clock, out InMemoryFeatureCache cache)
    {
        cache = new InMemoryFeatureCache(0);

        return new FeatureRepository(NullLogger<FeatureRepository>.Instance, cache, worker, null, clock.UtcNow);
    }

    private static GrowthBookRetrievalOptions Background => new GrowthBookRetrievalOptions();
    private static GrowthBookRetrievalOptions Blocking => new GrowthBookRetrievalOptions { WaitForCompletion = true };

    private sealed class FailingWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly SemaphoreSlim _attemptSignal = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public void Cancel() { }
        public bool WaitForAttempt() => _attemptSignal.Wait(TimeSpan.FromSeconds(5));

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _attemptSignal.Release();

            return Task.FromResult<IDictionary<string, Feature>>(null);
        }
    }

    /// <summary>
    /// A fetch that blocks until released, so a test can cancel one caller while another waits.
    /// </summary>
    private sealed class BlockingWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly SemaphoreSlim _entered = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public void Cancel() { }
        public bool WaitUntilEntered() => _entered.Wait(TimeSpan.FromSeconds(5));
        public void Release() => _release.Set();

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(5));

            return Task.FromResult<IDictionary<string, Feature>>(FeatureSet());
        }
    }

    [Fact]
    public async Task AForcedRefreshReportsTheFailureInsteadOfTheWarmCache()
    {
        var worker = new FailingWorker();
        var repository = CreateRepository(worker, new ManualClock(), out var cache);
        await cache.RefreshWith(FeatureSet());

        using var growthBook = new GrowthBook(new Context { FeatureRepository = repository });

        var result = await growthBook.RefreshFeatures();

        result.Success.Should().BeFalse(
            "a caller who asked for a refresh must not be told one succeeded when the request failed");
        worker.Attempts.Should().Be(1, "and the request must actually have been made before answering");
    }

    [Fact]
    public async Task AForcedRefreshStillReportsSuccessWhenTheRequestSucceeds()
    {
        var worker = new BlockingWorker();
        var repository = CreateRepository(worker, new ManualClock(), out var cache);
        await cache.RefreshWith(FeatureSet());

        worker.Release();

        using var growthBook = new GrowthBook(new Context { FeatureRepository = repository });

        var result = await growthBook.RefreshFeatures();

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OneCallerCancellingDoesNotFailTheOthersSharingTheFetch()
    {
        var worker = new BlockingWorker();
        var repository = CreateRepository(worker, new ManualClock(), out var cache);
        await cache.RefreshWith(FeatureSet());

        using var cancellation = new CancellationTokenSource();

        var cancelling = Task.Run(() => repository.GetFeatures(Blocking, cancellation.Token));
        worker.WaitUntilEntered().Should().BeTrue("because the shared fetch has to be under way");

        var joiner = repository.GetFeatures(Blocking);

        cancellation.Cancel();
        Func<Task> awaitCancelled = () => cancelling;
        await awaitCancelled.Should().ThrowAsync<OperationCanceledException>(
            "the caller that cancelled is the one that fails");

        worker.Release();

        var features = await joiner;

        features.Should().NotBeNull("a caller that did not cancel must still get its answer");
        worker.Attempts.Should().Be(1, "and the request itself is never cancelled on anyone's behalf");
    }

    [Fact]
    public async Task ACancelledCallerDoesNotOpenTheBackoffWindow()
    {
        var worker = new BlockingWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);
        await cache.RefreshWith(FeatureSet());

        using var cancellation = new CancellationTokenSource();

        var cancelling = Task.Run(() => repository.GetFeatures(Blocking, cancellation.Token));
        worker.WaitUntilEntered().Should().BeTrue();

        cancellation.Cancel();
        Func<Task> awaitCancelled = () => cancelling;
        await awaitCancelled.Should().ThrowAsync<OperationCanceledException>();

        worker.Release();

        // A healthy API must keep being polled: giving up is the caller's doing, not the API's.
        await repository.GetFeatures(Blocking);

        worker.Attempts.Should().Be(1,
            "the second call joined the fetch still in flight rather than being held off by a backoff window");
    }

    /// <summary>
    /// A worker that hands back a task the test completes, so the moment a fetch finishes is under
    /// the test's control rather than the scheduler's.
    /// </summary>
    private sealed class ControlledWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly List<TaskCompletionSource<IDictionary<string, Feature>>> _pending =
            new List<TaskCompletionSource<IDictionary<string, Feature>>>();

        private readonly SemaphoreSlim _entered = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        public void Cancel() { }

        /// <summary>
        /// Waits until a fetch has actually reached the worker, so a test never reads the count
        /// before the thread pool has run it.
        /// </summary>
        public bool WaitUntilEntered() => _entered.Wait(TimeSpan.FromSeconds(5));

        /// <summary>
        /// Gives a fetch a bounded chance to arrive, for asserting that none did.
        /// </summary>
        public void WaitForQuiet() => _entered.Wait(TimeSpan.FromMilliseconds(250));

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _entered.Release();

            var pending = new TaskCompletionSource<IDictionary<string, Feature>>();

            lock (_pending)
            {
                _pending.Add(pending);
            }

            return pending.Task;
        }

        public void Finish(int index, IDictionary<string, Feature> result)
        {
            lock (_pending)
            {
                _pending[index].SetResult(result);
            }
        }
    }

    /// <summary>
    /// A fetch that has finished but whose continuation has not run yet must still count. Without
    /// that, an evaluation arriving in the gap passes the gate and hits the unreachable API again.
    /// </summary>
    [Fact]
    public async Task AFetchThatFinishedButIsNotRecordedYetStillHoldsTheGate()
    {
        var worker = new ControlledWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);
        await cache.RefreshWith(FeatureSet());

        var first = repository.GetFeatures(Background);
        worker.WaitUntilEntered().Should().BeTrue("the first call starts a fetch");
        worker.Attempts.Should().Be(1);

        // Finish it as a failure without giving its continuation a chance to run.
        worker.Finish(0, null);
        await first;

        await repository.GetFeatures(Background);
        worker.WaitForQuiet();

        worker.Attempts.Should().Be(1,
            "the finished fetch is accounted for before the gate decides, so no second request starts");
    }
}
