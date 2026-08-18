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
}
