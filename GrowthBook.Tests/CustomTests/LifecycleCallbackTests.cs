using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using NSubstitute;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the teardown lifecycle surface. An instance could be disposed but there was no way to hook
/// into that or to ask whether it had happened, so wiring the SDK into a framework lifecycle - a DI
/// container releasing resources, a test asserting clean teardown - had nothing to attach to.
/// </summary>
public class LifecycleCallbackTests : UnitTest
{
    private static GrowthBook NewInstanceWithExperiment() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature>
        {
            ["experiment-flag"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Variations = new JArray(false, true),
                        Coverage = 1d,
                        Meta = new List<VariationMeta>
                        {
                            new VariationMeta { Key = "0" },
                            new VariationMeta { Key = "1" }
                        }
                    }
                }
            }
        }
    });

    private static GrowthBook NewInstance() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } }
    });

    [Fact]
    public void IsDestroyedIsFalseUntilTeardown()
    {
        var growthBook = NewInstance();

        growthBook.IsDestroyed.Should().BeFalse();

        growthBook.Destroy();

        growthBook.IsDestroyed.Should().BeTrue();
    }

    [Fact]
    public void DisposeFlipsTheFlagJustAsDestroyDoes()
    {
        var growthBook = NewInstance();

        growthBook.Dispose();

        growthBook.IsDestroyed.Should().BeTrue("Destroy is only an alias - both routes are the same teardown");
    }

    [Fact]
    public void ARegisteredCallbackRunsOnTeardown()
    {
        var growthBook = NewInstance();
        var calls = 0;

        growthBook.OnDestroy(() => calls++);

        calls.Should().Be(0, "registration alone must not run it");

        growthBook.Destroy();

        calls.Should().Be(1);
    }

    [Fact]
    public void EveryRegisteredCallbackRunsOnceAndInOrder()
    {
        var growthBook = NewInstance();
        var order = new List<string>();

        growthBook.OnDestroy(() => order.Add("first"));
        growthBook.OnDestroy(() => order.Add("second"));
        growthBook.OnDestroy(() => order.Add("third"));

        growthBook.Destroy();

        order.Should().Equal("first", "second", "third");
    }

    [Fact]
    public void ARepeatTeardownDoesNotFireThemAgain()
    {
        var growthBook = NewInstance();
        var calls = 0;

        growthBook.OnDestroy(() => calls++);

        growthBook.Destroy();
        growthBook.Destroy();
        growthBook.Dispose();

        calls.Should().Be(1, "exactly once");
    }

    /// <summary>
    /// The registered callbacks, read by reflection. A behavioural assertion cannot see whether they
    /// were released, because the guard in Dispose already stops a repeat teardown from reaching them.
    /// </summary>
    private static int RegisteredCallbackCount(GrowthBook growthBook)
    {
        var field = typeof(GrowthBook).GetField("_destroyCallbacks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        field.Should().NotBeNull("the test needs to see the registry to assert it was released");

        return ((System.Collections.ICollection)field.GetValue(growthBook)).Count;
    }

    [Fact]
    public void TeardownReleasesTheCallbacksItRan()
    {
        var growthBook = NewInstance();
        var captured = new byte[1024];

        growthBook.OnDestroy(() => GC.KeepAlive(captured));
        growthBook.OnDestroy(() => GC.KeepAlive(captured));

        RegisteredCallbackCount(growthBook).Should().Be(2);

        growthBook.Destroy();

        RegisteredCallbackCount(growthBook).Should().Be(0,
            "a callback closes over whatever the consumer captured, so a destroyed instance must not keep holding it");
    }

    [Fact]
    public void AThrowingCallbackDoesNotStopTheOthersOrTheTeardown()
    {
        var growthBook = NewInstance();
        var ran = new List<string>();

        growthBook.OnDestroy(() => ran.Add("before"));
        growthBook.OnDestroy(() => throw new InvalidOperationException("the consumer's callback is broken"));
        growthBook.OnDestroy(() => ran.Add("after"));

        Action teardown = () => growthBook.Destroy();

        teardown.Should().NotThrow();
        ran.Should().Equal(new[] { "before", "after" }, "a throwing callback is absorbed, the rest still run");
        growthBook.IsDestroyed.Should().BeTrue("and teardown still completed");
        growthBook.Features.Should().BeEmpty("including the part that releases state");
    }

    [Fact]
    public void ACallbackSeesTheInstanceAlreadyMarkedDestroyed()
    {
        var growthBook = NewInstance();
        var seen = false;

        growthBook.OnDestroy(() => seen = growthBook.IsDestroyed);

        growthBook.Destroy();

        seen.Should().BeTrue("a callback asking about the teardown it was called for must not be told it has not happened");
    }

    [Fact]
    public void ACallbackStillSeesTheStateItMayNeed()
    {
        var growthBook = NewInstanceWithExperiment();

        growthBook.EvalFeature("experiment-flag").ExperimentResult.Should().NotBeNull();
        growthBook.GetAllResults().Should().NotBeEmpty("the assignment is recorded before teardown starts");

        var featuresAtTeardown = -1;
        var resultsAtTeardown = -1;

        growthBook.OnDestroy(() =>
        {
            featuresAtTeardown = growthBook.Features.Count;
            resultsAtTeardown = growthBook.GetAllResults().Count;
        });

        growthBook.Destroy();

        featuresAtTeardown.Should().Be(1,
            "callbacks run before the state is released, so one that needs the features can still read them");
        resultsAtTeardown.Should().Be(1,
            "and the assignments it may need to report on are still there while it runs");
        growthBook.Features.Should().BeEmpty("and the release still happened afterwards");
        growthBook.GetAllResults().Should().BeEmpty("including the assignments, once the callbacks are done");
    }

    [Fact]
    public void RegisteringNullIsIgnored()
    {
        var growthBook = NewInstance();

        Action register = () => growthBook.OnDestroy(null);

        register.Should().NotThrow();

        Action teardown = () => growthBook.Destroy();

        teardown.Should().NotThrow("a null registration must not become a null reference during teardown");
    }

    [Fact]
    public void RegisteringAfterTeardownRunsTheCallbackImmediately()
    {
        var growthBook = NewInstance();

        growthBook.Destroy();

        var calls = 0;

        growthBook.OnDestroy(() => calls++);

        calls.Should().Be(1, "a callback registered against a destroyed instance would otherwise never run at all");

        growthBook.Destroy();

        calls.Should().Be(1, "and it must not be queued for a later teardown as well");
    }

    [Fact]
    public void TheSurfaceIsReachableThroughTheInterface()
    {
        IGrowthBook growthBook = NewInstance();
        var calls = 0;

        growthBook.IsDestroyed.Should().BeFalse();
        growthBook.OnDestroy(() => calls++);

        // Destroy is not on IGrowthBook on this branch, only Dispose - they are the same teardown.
        growthBook.Dispose();

        growthBook.IsDestroyed.Should().BeTrue("a DI-injected consumer only sees IGrowthBook");
        calls.Should().Be(1);
    }

    /// <summary>
    /// Reaches <c>Dispose(bool)</c> the way a finalizer would. There is no finalizer today, but the
    /// method is protected virtual and shaped as the canonical dispose pattern, so a derived type can.
    /// </summary>
    private sealed class FinalizableGrowthBook : GrowthBook
    {
        public FinalizableGrowthBook(Context context) : base(context) { }

        public void DisposeAsFinalizerWould() => Dispose(disposing: false);
    }

    [Fact]
    public void AFinalizerStyleDisposeDoesNotMarkTheInstanceDestroyed()
    {
        var growthBook = new FinalizableGrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" })
        });

        growthBook.DisposeAsFinalizerWould();

        growthBook.IsDestroyed.Should().BeFalse(
            "disposing == false means the finalizer, which releases no managed state - marking the instance " +
            "destroyed there would make a later real Dispose a no-op");
    }

    [Fact]
    public void AFinalizerStyleDisposeDoesNotConsumeTheCallbacks()
    {
        var growthBook = new FinalizableGrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" })
        });

        var calls = 0;
        growthBook.OnDestroy(() => calls++);

        growthBook.DisposeAsFinalizerWould();

        calls.Should().Be(0, "user callbacks must never run on the finalizer thread");

        growthBook.Dispose();

        calls.Should().Be(1, "and the real teardown must still be able to run them");
    }

    /// <summary>
    /// A repository whose Cancel throws, standing in for a consumer-supplied one - or the built-in
    /// path, where CancellationTokenSource.Cancel surfaces an AggregateException from a registered
    /// cancellation callback.
    /// </summary>
    private sealed class ThrowingRepository : IGrowthBookFeatureRepository
    {
        public void Cancel() => throw new InvalidOperationException("cancellation blew up");

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
            => Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>());

        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
            => GetFeatures(options, cancellationToken);

        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;
    }

    [Fact]
    public void AThrowingRepositoryCancelDoesNotEscapeTeardown()
    {
        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = new ThrowingRepository()
        });

        var calls = 0;
        growthBook.OnDestroy(() => calls++);

        Action teardown = () => growthBook.Destroy();

        teardown.Should().NotThrow(
            "the instance is already marked destroyed by then, so an escaping exception would strand it " +
            "with the rest of the teardown undone and no way to retry");

        calls.Should().Be(1);
        growthBook.IsDestroyed.Should().BeTrue();
    }

    [Fact]
    public void ARegisteredCallbackCanBeUnregistered()
    {
        var growthBook = NewInstance();
        var calls = 0;

        var registration = growthBook.OnDestroy(() => calls++);

        registration.Dispose();
        growthBook.Destroy();

        calls.Should().Be(0, "OnDestroy returns a handle like Subscribe does, and disposing it detaches the callback");
    }

    [Fact]
    public void UnregisteringOneCallbackLeavesTheOthers()
    {
        var growthBook = NewInstance();
        var ran = new List<string>();

        growthBook.OnDestroy(() => ran.Add("first"));
        var second = growthBook.OnDestroy(() => ran.Add("second"));
        growthBook.OnDestroy(() => ran.Add("third"));

        second.Dispose();
        growthBook.Destroy();

        ran.Should().Equal(new[] { "first", "third" });
    }

    [Fact]
    public void TheRegistrationHandleIsSafeToDisposeTwiceAndAfterTeardown()
    {
        var growthBook = NewInstance();
        var calls = 0;

        var registration = growthBook.OnDestroy(() => calls++);

        growthBook.Destroy();

        Action disposeLate = () =>
        {
            registration.Dispose();
            registration.Dispose();
        };

        disposeLate.Should().NotThrow();
        calls.Should().Be(1, "the callback already ran - unregistering afterwards changes nothing");
    }

    [Fact]
    public void RegisteringNullReturnsAHandleRatherThanNull()
    {
        var growthBook = NewInstance();

        var registration = growthBook.OnDestroy(null);

        registration.Should().NotBeNull("a caller writing 'using' around the result must not get a null reference");

        Action dispose = () => registration.Dispose();

        dispose.Should().NotThrow();
    }

    /// <summary>
    /// A callback can unregister another while the batch is already running. The list it was taken
    /// from has been replaced by then, so removing from the live list leaves the pending callback in
    /// the batch and it runs after the caller asked for it not to.
    /// </summary>
    [Fact]
    public void ACallbackCancelledByAnEarlierOneDoesNotRun()
    {
        var growthBook = NewInstance();
        var secondRan = false;

        IDisposable second = null;

        growthBook.OnDestroy(() => second.Dispose());
        second = growthBook.OnDestroy(() => secondRan = true);

        growthBook.Destroy();

        secondRan.Should().BeFalse(
            "because the handle was disposed before its callback was reached, and disposing a handle means it does not run");
    }

    /// <summary>
    /// Features is publicly writable, so a callback can leave it as something Clear cannot be called
    /// on. Teardown is already past the point of no return by then - nothing can retry it - so the
    /// resources still have to be released.
    /// </summary>
    [Fact]
    public void ACallbackBreakingTheStateDoesNotStopTheResourcesBeingReleased()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = true } },
            FeatureRepository = repository
        });

        growthBook.OnDestroy(() => growthBook.Features = null);

        Action destroy = () => growthBook.Destroy();

        destroy.Should().NotThrow("because teardown cannot be retried, so it has to finish");
        repository.Received(1).Cancel();
        growthBook.IsDestroyed.Should().BeTrue();
    }

    /// <summary>
    /// Two threads disposing at once could both get past the check before either set the flag. The
    /// one that found no callbacks left to run would then clear the state while the other's
    /// callbacks were still reading it. Hammered, since the window is only reachable by a race.
    /// </summary>
    [Fact]
    public void ConcurrentTeardownDoesNotClearTheStateACallbackIsReading()
    {
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            var growthBook = NewInstance();
            var featuresSeen = -1;

            growthBook.OnDestroy(() =>
            {
                // Long enough for a second teardown to reach the state clearing if it is going to.
                Thread.SpinWait(2000);
                featuresSeen = growthBook.Features?.Count ?? -1;
            });

            var ready = new ManualResetEventSlim(false);

            var racers = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(() => { ready.Wait(); growthBook.Destroy(); }))
                .ToArray();

            ready.Set();
            Task.WaitAll(racers, TimeSpan.FromSeconds(10));

            featuresSeen.Should().Be(1,
                "because only one teardown may run, and the callbacks it fires must still see the state they were promised");
        }
    }
}
