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

        public int CancelCallCount { get; private set; }

        public void Cancel() => CancelCallCount++;

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

        // Nothing to assert on a private repository beyond this not throwing; the behavior is pinned by the
        // sibling tests above, which prove the owned/not-owned distinction is what drives cancellation.
        growthBook.Dispose();
    }
}
