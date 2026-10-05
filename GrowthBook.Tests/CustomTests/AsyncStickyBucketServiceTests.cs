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
        public void Finish(int index)
        {
            string[] keys;

            lock (_pending)
            {
                keys = _requested[index];
            }

            var answer = keys.Where(_documents.ContainsKey).ToDictionary(key => key, key => _documents[key]);

            lock (_pending)
            {
                _pending[index].SetResult(answer);
            }
        }

        public Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default)
        {
            var pending = new TaskCompletionSource<IDictionary<string, StickyAssignmentsDocument>>();

            lock (_pending)
            {
                _requested.Add(attributes.ToArray());
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
}
