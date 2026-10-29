using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class AsyncStickyBucketServiceTests : UnitTest
{
    /// <summary>
    /// Stands in for a Redis/SQL-backed store. Deliberately yields before answering so a caller that
    /// tried to block on it synchronously would be doing exactly the thing this interface exists to avoid.
    /// </summary>
    private sealed class FakeAsyncStickyBucketService : IAsyncStickyBucketService
    {
        private readonly Dictionary<string, StickyAssignmentsDocument> _documents = new Dictionary<string, StickyAssignmentsDocument>();

        public int GetAllAssignmentsCallCount { get; private set; }
        public List<StickyAssignmentsDocument> SavedDocuments { get; } = new List<StickyAssignmentsDocument>();
        public Exception SaveException { get; set; }
        public Exception ReadException { get; set; }

        /// <summary>
        /// Answers null, which the interface defines as "the store could not be read" rather than
        /// "the store holds nothing".
        /// </summary>
        public bool ReportReadsAsUnanswerable { get; set; }

        public void Seed(StickyAssignmentsDocument document) => _documents[document.FormattedAttribute] = document;

        public async Task<StickyAssignmentsDocument> GetAssignmentsAsync(string attributeName, string attributeValue, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var key = new StickyAssignmentsDocument(attributeName, attributeValue).FormattedAttribute;
            return _documents.TryGetValue(key, out var document) ? document : null;
        }

        public async Task SaveAssignmentsAsync(StickyAssignmentsDocument document, CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            if (SaveException != null)
            {
                throw SaveException;
            }

            _documents[document.FormattedAttribute] = document;
            SavedDocuments.Add(document);
        }

        public async Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            GetAllAssignmentsCallCount++;

            if (ReadException != null)
            {
                throw ReadException;
            }

            if (ReportReadsAsUnanswerable)
            {
                return null;
            }

            return attributes
                .Where(_documents.ContainsKey)
                .ToDictionary(key => key, key => _documents[key]);
        }
    }

    private static Feature CreateExperimentFeature() => new Feature
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
    };

    [Fact]
    public void SettingBothStickyBucketServicesThrowsAtConstruction()
    {
        var context = new Context
        {
            StickyBucketService = new InMemoryStickyBucketService(),
            AsyncStickyBucketService = new FakeAsyncStickyBucketService()
        };

        Assert.Throws<ArgumentException>(() => new GrowthBook(context));
    }

    [Fact]
    public async Task LoadStickyBucketAssignmentsAsyncPullsAssignmentsFromTheAsyncStore()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService();
        service.Seed(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        await growthBook.LoadStickyBucketAssignmentsAsync();

        var result = growthBook.EvalFeature(FeatureName);
        result.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the assignment was pulled from the async store");
        result.On.Should().BeTrue("because the stored assignment points at variation index 1");
    }

    [Fact]
    public void ConstructorDoesNotReadTheAsyncStore()
    {
        var service = new FakeAsyncStickyBucketService();

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["test-feature"] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        growthBook.Should().NotBeNull();
        service.GetAllAssignmentsCallCount.Should().Be(0, "because the constructor can't await, so the caller has to load assignments explicitly");
    }

    [Fact]
    public async Task LoadStickyBucketAssignmentsAsyncIsANoOpWithoutAnAsyncService()
    {
        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["test-feature"] = CreateExperimentFeature() },
            StickyBucketService = new InMemoryStickyBucketService()
        };

        var growthBook = new GrowthBook(context);

        // Should complete without touching anything - the sync service path is unaffected.
        await growthBook.LoadStickyBucketAssignmentsAsync();
    }

    [Fact]
    public async Task AssignmentsAreVisibleImmediatelyAndPersistedToTheAsyncStore()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService();

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        var first = growthBook.EvalFeature(FeatureName);
        first.ExperimentResult.StickyBucketUsed.Should().BeFalse("because nothing was stored yet");

        // The in-memory docs are updated synchronously, so the very next evaluation honors the
        // assignment even though the store write hasn't necessarily completed.
        var second = growthBook.EvalFeature(FeatureName);
        second.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the assignment must be visible without waiting on the async write");
        second.ExperimentResult.VariationId.Should().Be(first.ExperimentResult.VariationId);

        // The dispatched write should land shortly after.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.SavedDocuments.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        service.SavedDocuments.Should().ContainSingle("because the assignment should have been persisted asynchronously");
        service.SavedDocuments[0].Assignments.Should().ContainKey($"{FeatureName}__0");
    }

    [Fact]
    public void AFailingAsyncSaveDoesNotBreakEvaluation()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService { SaveException = new InvalidOperationException("redis is down") };

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Should().NotBeNull("because a failing store write must not prevent the feature result from being returned");
        result.ExperimentResult.InExperiment.Should().BeTrue();
    }

    [Fact]
    public async Task LoadFeaturesRefreshesTheAsyncStickyBucketAssignments()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService();
        service.Seed(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var repository = new FakeFeatureRepository(new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() });

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            AsyncStickyBucketService = service,
            FeatureRepository = repository
        };

        var growthBook = new GrowthBook(context);

        await growthBook.LoadFeatures();

        service.GetAllAssignmentsCallCount.Should().BeGreaterThan(0, "because LoadFeatures should refresh async sticky bucket assignments");
        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed.Should().BeTrue();
    }

    [Fact]
    public async Task MergeAttributesAsyncRefreshesAssignmentsForTheNewIdentifier()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService();
        await service.SaveAssignmentsAsync(new StickyAssignmentsDocument(
            "id",
            "user-2",
            new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        // The reference SDK's setAttributes awaits refreshStickyBuckets, so the assignments for the new
        // identifier must be in place by the time this returns - no separate load call.
        await growthBook.MergeAttributesAsync(new { id = "user-2" });

        var result = growthBook.EvalFeature(FeatureName);

        result.ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "because rebinding to user-2 must pull that user's assignment without a separate LoadStickyBucketAssignmentsAsync call");
        result.On.Should().BeTrue("because user-2's stored assignment points at variation index 1");
    }

    /// <summary>
    /// A store that cannot be read is not a store that is empty. Replacing the loaded assignments with
    /// nothing on a failed read would unstick every user the instance had already bucketed, which is the
    /// one thing sticky bucketing exists to prevent.
    /// </summary>
    [Fact]
    public async Task AStoreThatCannotBeReadLeavesTheLoadedAssignmentsInPlace()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService();
        await service.SaveAssignmentsAsync(new StickyAssignmentsDocument(
            "id",
            "user-1",
            new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        await growthBook.LoadStickyBucketAssignmentsAsync();
        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "because the assignment was there to be loaded in the first place");

        service.ReportReadsAsUnanswerable = true;

        await growthBook.LoadStickyBucketAssignmentsAsync();

        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "because a store that could not answer must leave the assignments already held alone");
    }

    /// <summary>
    /// The features are applied before the sticky bucket refresh runs, so a store that is down has to leave
    /// the load reported as the success it was. Evaluation still works without the refreshed assignments.
    /// </summary>
    [Fact]
    public async Task AStickyBucketStoreFailureDoesNotFailAFeatureLoad()
    {
        const string FeatureName = "test-feature";

        var service = new FakeAsyncStickyBucketService { ReadException = new InvalidOperationException("the store is down") };

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service,
            FeatureRepository = new FakeFeatureRepository(new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() })
        };

        var growthBook = new GrowthBook(context);

        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeTrue("because the features were retrieved and applied before the store was ever consulted");
        growthBook.Features.Should().ContainKey(FeatureName);
    }

    private sealed class FakeFeatureRepository : IGrowthBookFeatureRepository
    {
        private readonly IDictionary<string, Feature> _features;

        public FakeFeatureRepository(IDictionary<string, Feature> features) => _features = features;

        public void Cancel() { }
        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) => Task.FromResult(_features);
        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null) => Task.FromResult(_features);
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }
        public bool IsAlreadyTracked(string trackingKey) => false;
        public void MarkAsTracked(string trackingKey) { }
        public bool TryMarkAsTracked(string trackingKey) => true;
    }

    /// <summary>
    /// A store whose reads finish when the test says so, and in whatever order it chooses, so an
    /// overlapping pair of loads can be made to come back the wrong way round on purpose.
    /// </summary>
    private sealed class GatedAsyncStickyBucketService : IAsyncStickyBucketService
    {
        private readonly Dictionary<string, StickyAssignmentsDocument> _documents = new Dictionary<string, StickyAssignmentsDocument>();

        private readonly List<TaskCompletionSource<IDictionary<string, StickyAssignmentsDocument>>> _pending =
            new List<TaskCompletionSource<IDictionary<string, StickyAssignmentsDocument>>>();

        private readonly List<string[]> _requested = new List<string[]>();

        private readonly List<IDictionary<string, StickyAssignmentsDocument>> _answers =
            new List<IDictionary<string, StickyAssignmentsDocument>>();

        public void Seed(StickyAssignmentsDocument document) => _documents[document.FormattedAttribute] = document;

        public int ReadCount
        {
            get
            {
                lock (_pending)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>
        /// Waits until at least <paramref name="count"/> reads have reached the store. An attribute change
        /// dispatches a refresh of its own, so the number of reads in flight is not simply the number the
        /// test started itself.
        /// </summary>
        public bool WaitForReadCount(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (ReadCount >= count)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }

        /// <summary>
        /// Completes the read at <paramref name="index"/> with whatever was stored for the keys it asked for.
        /// </summary>
        /// <summary>
        /// Completes a read with the answer captured when it was made, not with whatever the store
        /// holds now. A real read sees the store as it was at the time of the call.
        /// </summary>
        public void Finish(int index)
        {
            lock (_pending)
            {
                _pending[index].SetResult(_answers[index]);
            }
        }

        public Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default)
        {
            var pending = new TaskCompletionSource<IDictionary<string, StickyAssignmentsDocument>>();

            var keys = attributes.ToArray();
            var answer = keys.Where(_documents.ContainsKey).ToDictionary(key => key, key => _documents[key]);

            lock (_pending)
            {
                _requested.Add(keys);
                _answers.Add(answer);
                _pending.Add(pending);
            }

            return pending.Task;
        }

        public Task<StickyAssignmentsDocument> GetAssignmentsAsync(string attributeName, string attributeValue, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAssignmentsAsync(StickyAssignmentsDocument document, CancellationToken cancellationToken = default)
        {
            _documents[document.FormattedAttribute] = document;

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Two loads overlapping is ordinary: the identifier changes, a load starts, it changes again, another
    /// starts. If the first one comes back last it is holding the previous user's documents, and
    /// publishing them loses the current user's stored variation - who is then bucketed afresh.
    /// </summary>
    [Fact]
    public async Task AnOverlappingLoadThatFinishesLastDoesNotReplaceTheNewerUsersDocuments()
    {
        const string FeatureName = "sticky-feature";

        var service = new GatedAsyncStickyBucketService();
        service.Seed(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "0" }));
        service.Seed(new StickyAssignmentsDocument("id", "user-2", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        });

        var firstLoad = growthBook.LoadStickyBucketAssignmentsAsync();
        service.WaitForReadCount(1).Should().BeTrue("because the load for user-1 has to be under way");

        growthBook.Attributes = JObject.FromObject(new { id = "user-2" });

        var secondLoad = growthBook.LoadStickyBucketAssignmentsAsync();

        // Three reads in flight, not two: changing the attributes dispatches a refresh of its own. Both of
        // the later ones ask for user-2, so either answers the second load.
        service.WaitForReadCount(3).Should().BeTrue("because the loads for user-2 have to be under way too");

        // The newer ones land first, then the older one - the order the fix has to survive.
        for (var index = 1; index < service.ReadCount; index++)
        {
            service.Finish(index);
        }

        await secondLoad;

        service.Finish(0);
        await firstLoad;

        var result = growthBook.EvalFeature(FeatureName).ExperimentResult;

        result.StickyBucketUsed.Should().BeTrue(
            "because user-2's document was loaded and the late answer for user-1 must not have displaced it");
        result.Key.Should().Be("1", "and it has to be the variation stored for user-2, not user-1's");
    }

    [Fact]
    public async Task ALoadStillPublishesWhenNothingOvertookIt()
    {
        const string FeatureName = "sticky-feature";

        var service = new GatedAsyncStickyBucketService();
        service.Seed(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        });

        var load = growthBook.LoadStickyBucketAssignmentsAsync();
        service.WaitForReadCount(1).Should().BeTrue();
        service.Finish(0);
        await load;

        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "because dropping superseded loads must not stop an ordinary one from landing");
    }

    /// <summary>
    /// A refresh that read the store before a just-stored assignment was written there comes back
    /// without it, and replacing the whole set loses it. The load sequence orders refreshes against
    /// each other but says nothing about what was written locally while one was in flight.
    /// </summary>
    [Fact]
    public async Task ARefreshInFlightDoesNotDiscardAnAssignmentStoredWhileItRan()
    {
        const string FeatureName = "sticky-feature";

        var service = new GatedAsyncStickyBucketService();

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            AsyncStickyBucketService = service
        });

        // A refresh starts and reads the store while it still holds nothing.
        var refresh = growthBook.LoadStickyBucketAssignmentsAsync();
        service.WaitForReadCount(1).Should().BeTrue("the refresh has to be in flight");

        // Evaluating now assigns a variation and stores it locally.
        var assigned = growthBook.EvalFeature(FeatureName).ExperimentResult;
        assigned.Should().NotBeNull();

        // The refresh finishes, carrying the empty view it read before that assignment existed.
        service.Finish(0);
        await refresh;

        var afterRefresh = growthBook.EvalFeature(FeatureName).ExperimentResult;

        afterRefresh.StickyBucketUsed.Should().BeTrue(
            "the assignment was stored while the refresh was in flight, so the refresh never saw it and must not drop it");
        afterRefresh.Key.Should().Be(assigned.Key, "and the user keeps the variation they were given");
    }

    /// <summary>
    /// A store whose writes finish when the test says so, so the order they reach it in can be inspected.
    /// </summary>
    private sealed class GatedSaveAsyncStickyBucketService : IAsyncStickyBucketService
    {
        private readonly object _gate = new object();
        private readonly List<TaskCompletionSource<bool>> _pendingSaves = new List<TaskCompletionSource<bool>>();
        private readonly List<StickyAssignmentsDocument> _written = new List<StickyAssignmentsDocument>();

        /// <summary>
        /// How many writes have reached the store, whether or not they have been allowed to finish.
        /// </summary>
        public int SavesStarted
        {
            get
            {
                lock (_gate)
                {
                    return _pendingSaves.Count;
                }
            }
        }

        public IList<StickyAssignmentsDocument> Written
        {
            get
            {
                lock (_gate)
                {
                    return _written.ToList();
                }
            }
        }

        public bool WaitForSavesStarted(int count) => SpinUntil(() => SavesStarted >= count);

        public bool WaitForWritten(int count) => SpinUntil(() => Written.Count >= count);

        public void FinishSave(int index)
        {
            lock (_gate)
            {
                _pendingSaves[index].TrySetResult(true);
            }
        }

        private static bool SpinUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }

        public Task SaveAssignmentsAsync(StickyAssignmentsDocument document, CancellationToken cancellationToken = default)
        {
            var released = new TaskCompletionSource<bool>();

            lock (_gate)
            {
                _pendingSaves.Add(released);
            }

            return WriteWhenReleasedAsync(released.Task, document);
        }

        private async Task WriteWhenReleasedAsync(Task released, StickyAssignmentsDocument document)
        {
            await released.ConfigureAwait(false);

            lock (_gate)
            {
                _written.Add(document);
            }
        }

        public Task<StickyAssignmentsDocument> GetAssignmentsAsync(string attributeName, string attributeValue, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDictionary<string, StickyAssignmentsDocument>>(new Dictionary<string, StickyAssignmentsDocument>());
    }

    /// <summary>
    /// Every doc written for an identifier holds all of its assignments so far, so a later one is a
    /// superset of the one before it. Dispatching them independently lets a slow earlier write land last
    /// and replace the fuller doc with its own, and the assignments made in between are gone from the
    /// store - another instance, or this one after a restart, then buckets the user again for them.
    /// </summary>
    [Fact]
    public void WritesForTheSameIdentifierAreQueuedRatherThanDispatchedTogether()
    {
        var service = new GatedSaveAsyncStickyBucketService();

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>
            {
                ["first-feature"] = CreateExperimentFeature(),
                ["second-feature"] = CreateExperimentFeature()
            },
            AsyncStickyBucketService = service
        });

        // Two experiments for the one user, so the first doc holds one assignment and the second holds both.
        growthBook.EvalFeature("first-feature");
        growthBook.EvalFeature("second-feature");

        service.WaitForSavesStarted(1).Should().BeTrue("the first write should be dispatched straight away");

        Thread.Sleep(200);

        service.SavesStarted.Should().Be(1, "the second write has to wait behind the first, not race it");

        service.FinishSave(0);

        service.WaitForSavesStarted(2).Should().BeTrue("and it should be dispatched once the first has finished");

        service.FinishSave(1);

        service.WaitForWritten(2).Should().BeTrue();

        var written = service.Written;

        written[0].Assignments.Should().ContainKey("first-feature__0").And.HaveCount(1);
        written[1].Assignments.Should().ContainKeys("first-feature__0", "second-feature__0");
    }

    /// <summary>
    /// Nothing stops two experiments being evaluated for the same user at once. Reading the doc outside
    /// the lock let both start from the same one and each build a replacement holding only its own
    /// assignment, so whichever published second dropped the other's.
    /// </summary>
    [Fact]
    public void ConcurrentEvaluationsForTheSameUserKeepBothAssignments()
    {
        const int Rounds = 500;

        for (var round = 0; round < Rounds; round++)
        {
            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                Features = new Dictionary<string, Feature>
                {
                    ["first-feature"] = CreateExperimentFeature(),
                    ["second-feature"] = CreateExperimentFeature()
                },
                AsyncStickyBucketService = new FakeAsyncStickyBucketService()
            });

            using var start = new Barrier(2);

            var threads = new[] { "first-feature", "second-feature" }
                .Select(name => new Thread(() =>
                {
                    start.SignalAndWait();
                    growthBook.EvalFeature(name);
                }))
                .ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            // Re-evaluating has to find both assignments. A dropped one is bucketed afresh instead, which
            // is what StickyBucketUsed reports - the variation itself is the same either way.
            growthBook.EvalFeature("first-feature").ExperimentResult.StickyBucketUsed
                .Should().BeTrue($"round {round} stored an assignment for the first experiment");

            growthBook.EvalFeature("second-feature").ExperimentResult.StickyBucketUsed
                .Should().BeTrue($"round {round} stored an assignment for the second experiment");
        }
    }
}
