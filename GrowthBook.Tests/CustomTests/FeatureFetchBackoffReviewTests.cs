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

    /// <summary>
    /// A caller abandoning its own wait says nothing about the API, so it must not count as a failed
    /// fetch. The cancelled caller is deliberately not the one that finishes the fetch: a joiner awaits
    /// the same task, so by the time it returns the fetch has provably completed and the call that
    /// follows meets the gate rather than joining something still in flight.
    /// </summary>
    [Fact]
    public async Task ACancelledCallerDoesNotOpenTheBackoffWindow()
    {
        var worker = new ControlledWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);
        await cache.RefreshWith(FeatureSet());

        using var cancellation = new CancellationTokenSource();

        var cancelling = Task.Run(() => repository.GetFeatures(Blocking, cancellation.Token));
        worker.WaitUntilEntered().Should().BeTrue();

        var joiner = repository.GetFeatures(Blocking);

        cancellation.Cancel();
        Func<Task> awaitCancelled = () => cancelling;
        await awaitCancelled.Should().ThrowAsync<OperationCanceledException>();

        // The API is healthy: this fetch succeeds. Only the cancellation could open a window here.
        worker.Finish(0, FeatureSet());
        await joiner;

        await repository.GetFeatures(Background);
        worker.WaitUntilEntered().Should().BeTrue(
            "a healthy API must keep being polled; giving up was the caller's doing, not the API's");

        worker.Attempts.Should().Be(2);
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
    /// A scheduler that queues work and runs it only when the test says so, so a test can hold a
    /// caller suspended partway through and decide what happens in the meantime.
    /// </summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly List<Task> _queued = new List<Task>();

        protected override void QueueTask(Task task)
        {
            lock (_queued)
            {
                _queued.Add(task);
            }
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_queued)
            {
                return _queued.ToArray();
            }
        }

        /// <summary>
        /// Runs what is queued right now, not what those tasks go on to queue in turn.
        /// </summary>
        public int RunQueued()
        {
            Task[] batch;

            lock (_queued)
            {
                batch = _queued.ToArray();
                _queued.Clear();
            }

            foreach (var task in batch)
            {
                TryExecuteTask(task);
            }

            return batch.Length;
        }
    }

    /// <summary>
    /// A worker whose answers are decided up front and handed back already completed, so a fetch
    /// never suspends and the only thing a test has to schedule is the caller.
    /// </summary>
    private sealed class ScriptedWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly IDictionary<string, Feature>[] _answers;
        private int _attempts;

        public ScriptedWorker(params IDictionary<string, Feature>[] answers) => _answers = answers;

        public int Attempts => Volatile.Read(ref _attempts);
        public void Cancel() { }

        /// <summary>
        /// Waits for a fetch that a fire-and-forget call only promised, so the count is never read
        /// before the thread pool has got to it.
        /// </summary>
        public bool WaitForAttempts(int count) =>
            SpinWait.SpinUntil(() => Attempts >= count, TimeSpan.FromSeconds(5));

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            var index = Interlocked.Increment(ref _attempts) - 1;

            return Task.FromResult(index < _answers.Length ? _answers[index] : null);
        }
    }

    /// <summary>
    /// An outcome that arrives after a newer fetch has already been accounted for must be dropped.
    /// Recording it would reopen a backoff window that a success had closed, leaving a healthy API
    /// unpolled for as long as the stale window had left to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The late caller is held on a scheduler the test pumps by hand, which is what makes the ordering
    /// a decision rather than a race: it is suspended between its fetch failing and its recording of
    /// that failure, and a second, successful fetch is driven to completion in that gap.
    /// </para>
    /// <para>
    /// Two things keep this right, and this pins the outcome rather than either one of them: outcomes
    /// older than the last recorded one are dropped, and the gate accounts for the newest finished
    /// fetch before it decides, which repairs the state a stale record left behind. Removing both
    /// fails this test; removing either alone does not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnOutcomeOvertakenByANewerOneIsDropped()
    {
        var worker = new ScriptedWorker(null, FeatureSet(), FeatureSet());
        var repository = CreateRepository(worker, new ManualClock(), out var cache);
        await cache.RefreshWith(FeatureSet());

        // xUnit installs a SynchronizationContext, and an await prefers one of those over the ambient
        // scheduler, which would hand the caller back to the test runner instead of to the scheduler
        // this test pumps. Without this the caller cannot be held suspended at all.
        var testContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            var scheduler = new ManualScheduler();

            var late = Task.Factory
                .StartNew(() => repository.GetFeatures(Blocking), CancellationToken.None, TaskCreationOptions.None, scheduler)
                .Unwrap();

            // Run the call, then its fetch, then one more step so the fetch's answer reaches the frame
            // below the caller. The caller's own resumption, which is what records the failure, stays
            // queued. How many steps that is depends on the async machinery, so it is discovered rather
            // than hard-coded; both assertions below fail loudly if this lands anywhere else.
            while (worker.Attempts == 0 && scheduler.RunQueued() > 0)
            {
            }

            scheduler.RunQueued();

            late.IsCompleted.Should().BeFalse(
                "the caller has to still be suspended before it records, which is the point of this test");

            // In the gap: a forced refresh accounts for the failed fetch, makes its own, and succeeds.
            var forced = await repository.GetFeatures(new GrowthBookRetrievalOptions { ForceRefresh = true, WaitForCompletion = true });

            forced.Should().NotBeNull();
            worker.Attempts.Should().Be(2, "the failed fetch had finished, so this is a new request rather than a join");

            // Only now does the first caller get to record a failure that a later success has overtaken.
            scheduler.RunQueued();
            await late;

            await repository.GetFeatures(Background);

            worker.WaitForAttempts(3).Should().BeTrue(
                "the stale failure must not reopen the window that the later success closed");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    /// <summary>
    /// A fetch whose outcome has not been recorded yet must still count against the gate.
    /// </summary>
    /// <remarks>
    /// The fetch here is orphaned rather than merely slow to record: its only caller cancelled, which
    /// now records nothing, and a blocking caller leaves no continuation behind. So the failure is
    /// recorded by nothing except the gate accounting for it, which makes the test deterministic
    /// instead of a race against a continuation. This state is not contrived - it is exactly what a
    /// cancelled blocking caller leaves behind.
    /// </remarks>
    [Fact]
    public async Task AFetchThatFinishedButIsNotRecordedYetStillHoldsTheGate()
    {
        var worker = new ControlledWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);
        await cache.RefreshWith(FeatureSet());

        using var cancellation = new CancellationTokenSource();

        var cancelling = Task.Run(() => repository.GetFeatures(Blocking, cancellation.Token));
        worker.WaitUntilEntered().Should().BeTrue("the first call starts a fetch");
        worker.Attempts.Should().Be(1);

        cancellation.Cancel();
        Func<Task> awaitCancelled = () => cancelling;
        await awaitCancelled.Should().ThrowAsync<OperationCanceledException>();

        // The API is unreachable. Nothing is left to record this, since the only caller walked away.
        worker.Finish(0, null);

        // Completing the fetch is a thread pool hop the test cannot observe, the fetch having been
        // deliberately left with no caller and no continuation. Overshooting it costs a moment.
        await Task.Delay(250);

        await repository.GetFeatures(Background);
        worker.WaitForQuiet();

        worker.Attempts.Should().Be(1,
            "the finished fetch is accounted for before the gate decides, so no second request starts");
    }
}
