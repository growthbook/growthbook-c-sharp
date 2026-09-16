using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the retry backoff on feature fetching, and the force-refresh facade.
/// Before the backoff a failed fetch left the cache untouched, so <c>IsCacheExpired</c> stayed true and
/// the very next call fired another request - one HTTP request per feature evaluation while the API was
/// unreachable.
/// </summary>
public class FeatureFetchBackoffTests
{
    private sealed class ScriptedWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly Func<int, IDictionary<string, Feature>> _resultForAttempt;

        public ScriptedWorker(Func<int, IDictionary<string, Feature>> resultForAttempt) => _resultForAttempt = resultForAttempt;

        private readonly SemaphoreSlim _attemptSignal = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Cancel() { }

        /// <summary>
        /// Waits until the given number of attempts have been made, so a test never asserts on
        /// fire-and-forget work that has not run yet. Returns false if they do not arrive in time.
        /// </summary>
        public bool WaitForAttempts(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!_attemptSignal.Wait(TimeSpan.FromSeconds(5)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gives any in-flight attempt a bounded chance to land, for asserting that none was made.
        /// </summary>
        public void WaitForQuiet() => _attemptSignal.Wait(TimeSpan.FromMilliseconds(250));

        public Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _attemptSignal.Release();

            var result = _resultForAttempt(Attempts);

            if (result == null)
            {
                return Task.FromResult<IDictionary<string, Feature>>(null);
            }

            return Task.FromResult(result);
        }
    }

    private sealed class ManualClock
    {
        private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public DateTime UtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private static Dictionary<string, Feature> FeatureSet() =>
        new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } };

    // A zero second time to live keeps the cache permanently stale while still holding features, which is
    // the state the backoff exists for. The cache reads the system clock on this branch, so the manual clock
    // drives the repository's backoff only.
    private static FeatureRepository CreateRepository(IGrowthBookFeatureRefreshWorker worker, ManualClock clock, out InMemoryFeatureCache cache, int cacheExpirationInSeconds = 0)
    {
        cache = new InMemoryFeatureCache(cacheExpirationInSeconds);

        return new FeatureRepository(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRepository>.Instance,
            cache,
            worker,
            null,
            clock.UtcNow);
    }

    private static GrowthBookRetrievalOptions Background => new GrowthBookRetrievalOptions();

    private static GrowthBookRetrievalOptions Blocking => new GrowthBookRetrievalOptions { WaitForCompletion = true };

    [Fact]
    public async Task RepeatedCallsWhileTheApiIsDownDoNotFireARequestEachTime()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        for (var i = 0; i < 20; i++)
        {
            await repository.GetFeatures(Blocking);
        }

        worker.Attempts.Should().Be(1,
            "because after the first failure the backoff window holds off the rest - otherwise an unreachable API is hit once per evaluation");
    }

    [Fact]
    public async Task TheNextAttemptIsAllowedOnceTheWindowElapses()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        await repository.GetFeatures(Blocking);
        await repository.GetFeatures(Blocking);
        worker.Attempts.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(5));

        await repository.GetFeatures(Blocking);

        worker.Attempts.Should().Be(2, "because the first window is a couple of seconds at most");
    }

    [Fact]
    public async Task TheWindowGrowsWithEachConsecutiveFailure()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        for (var i = 0; i < 6; i++)
        {
            await repository.GetFeatures(Blocking);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        worker.Attempts.Should().BeLessThan(6,
            "because a five second gap stops being enough once the window has grown past it");
    }

    [Fact]
    public async Task TheClientNeverStopsRetryingAltogether()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            await repository.GetFeatures(Blocking);
        }

        worker.Attempts.Should().Be(12,
            "because the reference SDK backs off without an attempt limit - giving up would leave the caller on a stale cache with no way back");
    }

    [Fact]
    public async Task TheWindowIsCappedSoAnOutageDoesNotPushTheNextAttemptOutIndefinitely()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            await repository.GetFeatures(Blocking);
        }

        worker.Attempts.Should().Be(20, "because six minutes clears the five minute ceiling every time");
    }

    [Fact]
    public async Task ASuccessfulFetchClearsTheBackoff()
    {
        var worker = new ScriptedWorker(attempt => attempt <= 2 ? null : FeatureSet());
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        await repository.GetFeatures(Blocking);
        clock.Advance(TimeSpan.FromSeconds(10));
        await repository.GetFeatures(Blocking);
        clock.Advance(TimeSpan.FromSeconds(10));
        await repository.GetFeatures(Blocking);

        worker.Attempts.Should().Be(3);

        clock.Advance(TimeSpan.FromMinutes(5));
        await repository.GetFeatures(Blocking);

        worker.Attempts.Should().Be(4, "because the counter resets on success rather than staying stretched");
    }

    [Fact]
    public async Task AnEmptyCacheStillAttemptsRegardlessOfTheBackoff()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out _);

        await repository.GetFeatures(Blocking);
        await repository.GetFeatures(Blocking);

        worker.Attempts.Should().Be(2,
            "because with nothing cached there is no stale value to serve, so suppressing the attempt would return nothing at all");
    }

    [Fact]
    public async Task ForceRefreshStillGoesToTheApiWhenTheCacheIsFresh()
    {
        var worker = new ScriptedWorker(_ => FeatureSet());
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache, cacheExpirationInSeconds: 60);

        await cache.RefreshWith(FeatureSet());
        cache.IsCacheExpired.Should().BeFalse();

        await repository.GetFeatures(Background);
        worker.WaitForQuiet();
        worker.Attempts.Should().Be(0, "because a fresh cache needs no refresh");

        await repository.GetFeatures(new GrowthBookRetrievalOptions { ForceRefresh = true });
        worker.WaitForAttempts(1).Should().BeTrue();

        worker.Attempts.Should().Be(1, "because force refresh ignores cache freshness");
    }

    [Fact]
    public async Task ForceRefreshServesTheCachedValuesWhileTheRequestIsInFlight()
    {
        var worker = new ScriptedWorker(_ => FeatureSet());
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        var features = await repository.GetFeatures(new GrowthBookRetrievalOptions { ForceRefresh = true });

        features.Should().ContainKey("flag", "because evaluation must not block on the forced request");
    }

    [Fact]
    public async Task RefreshFeaturesForcesARequest()
    {
        var worker = new ScriptedWorker(_ => FeatureSet());
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = repository
        });

        var result = await growthBook.RefreshFeatures();

        result.Success.Should().BeTrue();
        worker.WaitForAttempts(1).Should().BeTrue();
        worker.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RefreshFeaturesWithoutForceRespectsCacheFreshness()
    {
        var worker = new ScriptedWorker(_ => FeatureSet());
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache, cacheExpirationInSeconds: 60);

        await cache.RefreshWith(FeatureSet());

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = repository
        });

        await growthBook.RefreshFeatures(force: false);
        worker.WaitForQuiet();

        worker.Attempts.Should().Be(0, "because without force the fresh cache is enough");
    }

    [Fact]
    public async Task ABlockingCallStillSurfacesTheFailureToTheCaller()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out _);

        var features = await repository.GetFeatures(new GrowthBookRetrievalOptions { WaitForCompletion = true });

        features.Should().BeNull("because the backoff must not turn a failed fetch into a silent success");
    }

    /// <summary>
    /// A worker whose fetch blocks until the test releases it, so several callers are provably in flight at
    /// the same moment.
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
    public async Task ConcurrentCallersShareASingleFetch()
    {
        var worker = new BlockingWorker();
        var cache = new InMemoryFeatureCache(cacheExpirationInSeconds: 0);
        await cache.RefreshWith(FeatureSet());

        var repository = new FeatureRepository(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureRepository>.Instance,
            cache, worker, null, new ManualClock().UtcNow);

        var calls = new Task[8];

        for (var i = 0; i < calls.Length; i++)
        {
            calls[i] = repository.GetFeatures(Blocking);
        }

        worker.WaitUntilEntered().Should().BeTrue("because at least one fetch has to start");
        worker.Release();

        await Task.WhenAll(calls);

        worker.Attempts.Should().Be(1,
            "because callers arriving together must join the fetch already in flight rather than each starting their own");
    }

    /// <summary>
    /// A worker whose fetch ends the way HttpClient's Timeout does: an OperationCanceledException that
    /// escapes an async method, which completes the task as Canceled rather than Faulted.
    /// </summary>
    private sealed class TimingOutWorker : IGrowthBookFeatureRefreshWorker
    {
        private readonly SemaphoreSlim _attemptSignal = new SemaphoreSlim(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public void Cancel() { }

        public bool WaitForAttempts(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!_attemptSignal.Wait(TimeSpan.FromSeconds(5)))
                {
                    return false;
                }
            }

            return true;
        }

        public void WaitForQuiet() => _attemptSignal.Wait(TimeSpan.FromMilliseconds(250));

        private readonly SemaphoreSlim _finishedSignal = new SemaphoreSlim(0);

        /// <summary>
        /// Waits until a fetch has actually ended, not merely started. Without this a following call
        /// joins the still-running task, so the test would be measuring fetch de-duplication rather
        /// than how the finished task was classified.
        /// </summary>
        public bool WaitUntilFinished() => _finishedSignal.Wait(TimeSpan.FromSeconds(5));

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            Interlocked.Increment(ref _attempts);
            _attemptSignal.Release();

            try
            {
                await Task.Yield();

                throw new TaskCanceledException("the request timed out");
            }
            finally
            {
                _finishedSignal.Release();
            }
        }
    }

    [Fact]
    public async Task ATimedOutFetchCountsAsAFailureAndOpensTheBackoffWindow()
    {
        var worker = new TimingOutWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        await repository.GetFeatures(Background);
        worker.WaitForAttempts(1).Should().BeTrue();
        worker.WaitUntilFinished().Should().BeTrue();

        // The continuation that records the outcome runs after the task completes.
        await Task.Delay(200);

        // A cancelled task is not a faulted one, so classifying on IsFaulted alone read this as a success,
        // left the failure count at zero, and let the very next call fire again.
        await repository.GetFeatures(Background);
        worker.WaitForQuiet();

        worker.Attempts.Should().Be(1, "the timeout has to open the backoff window like any other failure");
    }

    [Fact]
    public async Task AForcedRefreshIsNotSuppressedByTheBackoffWindow()
    {
        var worker = new ScriptedWorker(_ => null);
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        await repository.GetFeatures(Background);
        worker.WaitForAttempts(1).Should().BeTrue();

        await repository.GetFeatures(Background);
        worker.WaitForQuiet();
        worker.Attempts.Should().Be(1, "the automatic refresh is the one the window suppresses");

        await repository.GetFeatures(new GrowthBookRetrievalOptions { ForceRefresh = true });
        worker.WaitForAttempts(1).Should().BeTrue(
            "a caller who asked for a refresh outright is owed an attempt - suppressing it still reported " +
            "success, telling them a fetch happened when none did");
    }

    /// <summary>
    /// Blocks until released and then fails, so several callers can be held on one in-flight fetch whose
    /// outcome is a failure. <see cref="BlockingWorker"/> always succeeds, and a success would reset the
    /// very counter this is about.
    /// </summary>
    private sealed class BlockingFailingWorker : IGrowthBookFeatureRefreshWorker
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

            return Task.FromResult<IDictionary<string, Feature>>(null);
        }
    }

    [Fact]
    public async Task JoinersOfOneFailedFetchAdvanceTheWindowOnlyOnce()
    {
        var worker = new BlockingFailingWorker();
        var clock = new ManualClock();
        var repository = CreateRepository(worker, clock, out var cache);

        await cache.RefreshWith(FeatureSet());

        var callers = new List<Task<IDictionary<string, Feature>>>();

        for (var i = 0; i < 8; i++)
        {
            callers.Add(Task.Run(() => repository.GetFeatures(Blocking)));
        }

        worker.WaitUntilEntered().Should().BeTrue();
        worker.Release();

        await Task.WhenAll(callers);

        worker.Attempts.Should().Be(1, "the eight callers shared one fetch");

        // One failed request must advance the window by one step, not eight. At eight the window is
        // minutes long, so the difference is observable as a suppressed attempt after a short wait.
        clock.Advance(TimeSpan.FromSeconds(5));

        await repository.GetFeatures(Background);

        // The background path returns before the fetch has even started, so the attempt has to be waited
        // for rather than read straight away.
        worker.WaitUntilEntered().Should().BeTrue(
            "one failure means a window of roughly a second or two, which five seconds clears - eight " +
            "increments would have pushed it into minutes");

        worker.Attempts.Should().Be(2);
    }
}
