using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Pins the synchronous evaluation APIs against the deadlock they cause under a single-threaded
/// synchronization context: classic ASP.NET, WPF and WinForms all install one. The SDK blocks on its
/// own load inside EvalFeature(alwaysLoadFeatures: true), so if anything in the load posts its
/// continuation back to the blocked thread, that continuation never runs and the call never returns.
/// ConfigureAwait(false) settles that for the SDK's own awaits only - a feature repository supplied on
/// the Context is the application's code and follows its own rules.
/// </summary>
public class SynchronizationContextDeadlockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A context with one thread and one queue, like the ones the affected hosts install. A callback
    /// that blocks stops the queue being pumped, which is the whole mechanism of the deadlock.
    /// </summary>
    private sealed class SingleThreadedSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
            new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

        public override void Post(SendOrPostCallback callback, object state)
        {
            _queue.Add(new KeyValuePair<SendOrPostCallback, object>(callback, state));
        }

        public override void Send(SendOrPostCallback callback, object state)
        {
            callback(state);
        }

        public void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work.Key(work.Value);
            }
        }

        public void Stop()
        {
            _queue.CompleteAdding();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread that has a single-threaded context installed, and
    /// reports whether it finished rather than hanging the test run.
    /// </summary>
    /// <summary>
    /// Runs asynchronous work on the context thread itself, so its awaits resume through the
    /// installed context rather than on the thread pool.
    /// </summary>
    private static bool RunUnderSingleThreadedContext(Func<Task> work)
    {
        var finished = new ManualResetEventSlim(false);
        var context = new SingleThreadedSynchronizationContext();

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);

            context.Post(async _ =>
            {
                try
                {
                    await work();
                    finished.Set();
                }
                finally
                {
                    context.Stop();
                }
            }, null);

            context.Pump();
        });

        thread.IsBackground = true;
        thread.Start();

        return finished.Wait(Patience);
    }

    private static bool RunUnderSingleThreadedContext(Action work)
    {
        var finished = new ManualResetEventSlim(false);
        var context = new SingleThreadedSynchronizationContext();

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);

            context.Post(_ =>
            {
                try
                {
                    work();
                    finished.Set();
                }
                finally
                {
                    context.Stop();
                }
            }, null);

            context.Pump();
        });

        thread.IsBackground = true;
        thread.Start();

        return finished.Wait(Patience);
    }

    /// <summary>
    /// An application's own repository, which knows nothing about the SDK's threading rules and so
    /// awaits the ordinary way.
    /// </summary>
    private sealed class ContextCapturingRepository : IGrowthBookFeatureRepository
    {
        private readonly IDictionary<string, Feature> _features =
            new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } };

        // Required by the interface as of this branch. Never raised: this double answers from a
        // fixed map and never refreshes, which is the whole point of it.
#pragma warning disable CS0067
        public event EventHandler<FeaturesRefreshedEventArgs> FeaturesRefreshed;
#pragma warning restore CS0067

        public async Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            await Task.Delay(10);

            return _features;
        }

        public async Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            await Task.Delay(10);

            return _features;
        }

        public void Cancel() { }
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;
    }

    private static GrowthBook CreateGrowthBook()
    {
        return new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = new ContextCapturingRepository()
        });
    }

    [Fact]
    public void EvalFeatureWithAnAlwaysLoadDoesNotDeadlockOnAnInjectedRepository()
    {
        using var growthBook = CreateGrowthBook();
        FeatureResult result = null;

        var finished = RunUnderSingleThreadedContext(() => result = growthBook.EvalFeature("flag", alwaysLoadFeatures: true));

        finished.Should().BeTrue(
            "because the load must not need the thread it is blocking - a repository on the Context is the caller's own code and posts its continuations wherever its awaits say");
        result.On.Should().BeTrue("and the features it loaded have to be the ones evaluated");
    }

    [Fact]
    public void GetFeatureValueWithAnAlwaysLoadDoesNotDeadlockOnAnInjectedRepository()
    {
        using var growthBook = CreateGrowthBook();
        var value = false;

        var finished = RunUnderSingleThreadedContext(() => value = growthBook.GetFeatureValue("flag", false, alwaysLoadFeatures: true));

        finished.Should().BeTrue("because the same blocking load sits behind this overload");
        value.Should().BeTrue();
    }

    /// <summary>
    /// The async API awaited the ordinary way is the supported route under one of these contexts.
    /// A caller who blocks on it instead is doing the blocking itself, and the SDK has no say in how
    /// an injected repository resumes from there - which is precisely why the synchronous overloads
    /// above have to start the load off this thread rather than rely on the repository's awaits.
    /// </summary>
    [Fact]
    public void TheAsyncApiCompletesWhenItIsAwaited()
    {
        using var growthBook = CreateGrowthBook();
        var count = 0;
        SynchronizationContext resumedOn = null;

        // Awaited on the context thread itself. Going through Task.Run would take the context away
        // before the load started, leaving the thread merely blocked on a worker - which is not the
        // scenario this is here to cover.
        var finished = RunUnderSingleThreadedContext(async () =>
        {
            await growthBook.LoadFeatures();

            resumedOn = SynchronizationContext.Current;
            count = growthBook.Features.Count;
        });

        finished.Should().BeTrue("awaiting the async API on one of these contexts has to complete");
        count.Should().Be(1);
        resumedOn.Should().BeOfType<SingleThreadedSynchronizationContext>(
            "and the caller resumes on the context it called from, which is what an awaiting caller expects");
    }


    /// <summary>
    /// Blocking on the async API is the other half of the same problem. The synchronous entry points go
    /// through Task.Run, which takes the context away before the load starts; the async ones do not, so
    /// their innermost await is the one whose continuation is queued back to the blocked thread.
    /// </summary>
    [Theory]
    [InlineData("EvalFeatureAsync")]
    [InlineData("GetFeatureValueAsync")]
    [InlineData("IsOnAsync")]
    [InlineData("IsOffAsync")]
    public void BlockingOnTheAsyncApiUnderAContextStillCompletes(string method)
    {
        using var growthBook = CreateGrowthBook();

        var finished = RunUnderSingleThreadedContext(() =>
        {
            switch (method)
            {
                case "EvalFeatureAsync":
                    growthBook.EvalFeatureAsync("flag").GetAwaiter().GetResult();
                    break;
                case "GetFeatureValueAsync":
                    growthBook.GetFeatureValueAsync("flag", false).GetAwaiter().GetResult();
                    break;
                case "IsOnAsync":
                    growthBook.IsOnAsync("flag").GetAwaiter().GetResult();
                    break;
                default:
                    growthBook.IsOffAsync("flag").GetAwaiter().GetResult();
                    break;
            }
        });

        finished.Should().BeTrue(
            $"blocking on {method} from a single-threaded context must not wait on a continuation queued to the thread doing the blocking");
    }

    [Fact]
    public void TheAsyncApiStillReturnsTheRightAnswerUnderAContext()
    {
        using var growthBook = CreateGrowthBook();

        var on = false;

        var finished = RunUnderSingleThreadedContext(() =>
        {
            on = growthBook.IsOnAsync("flag").GetAwaiter().GetResult();
        });

        finished.Should().BeTrue();
        on.Should().BeTrue("not deadlocking is only half of it - the load still has to have happened");
    }

    [Fact]
    public void EvaluationStillWorksWithNoSynchronizationContextInstalled()
    {
        using var growthBook = CreateGrowthBook();

        growthBook.EvalFeature("flag", alwaysLoadFeatures: true).On.Should().BeTrue(
            "because the hosts without a context - ASP.NET Core, console apps, test runners - are the common case and must not regress");
    }
}
