using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// An instance loads the user's sticky assignments from the service, as <c>refreshStickyBuckets</c> does in the
/// JavaScript SDK: when it is built, when features are loaded, and when the attributes change. It used to hold only
/// the documents handed over on the context and the ones it saved itself, so a user seen by another instance, or seen
/// again after their identifier changed, was hashed again.
/// </summary>
public class StickyBucketRefreshTests
{
    /// <summary>Hashing picks the first variation whose weight is 1, so any other answer came from a saved assignment.</summary>
    private static Dictionary<string, Feature> Features(double firstWeight, double secondWeight) => new Dictionary<string, Feature>
    {
        ["feature"] = ExperimentFeature("exp", firstWeight, secondWeight)
    };

    private static Feature ExperimentFeature(string experimentKey, double firstWeight, double secondWeight) => new Feature
    {
        DefaultValue = JToken.FromObject(-1),
        Rules = new List<FeatureRule>
        {
            new FeatureRule
            {
                Key = experimentKey,
                Variations = new JArray(0, 1),
                Weights = new List<double> { firstWeight, secondWeight },
                Coverage = 1,
                HashAttribute = "id",
                Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
            }
        }
    };

    private static GrowthBookSdk.GrowthBook Create(IStickyBucketService service, string userId, Dictionary<string, Feature> features,
        IDictionary<string, StickyAssignmentsDocument> documents = null, IGrowthBookFeatureRepository repository = null) =>
        new GrowthBookSdk.GrowthBook(new Context
        {
            Attributes = new JObject { ["id"] = userId },
            Features = features,
            StickyBucketService = service,
            StickyBucketAssignmentDocs = documents,
            FeatureRepository = repository,
            LoggerFactory = NullLoggerFactory.Instance
        });

    private static StickyAssignmentsDocument Stored(string userId, string variation) =>
        new StickyAssignmentsDocument("id", userId, new Dictionary<string, string> { ["exp__0"] = variation });

    [Fact]
    public void AnInstanceFindsTheAssignmentAnotherInstanceStored()
    {
        var service = new InMemoryStickyBucketService();

        using (var first = Create(service, "user-1", Features(1, 0)))
        {
            first.GetFeatureValue("feature", -1).Should().Be(0);
        }

        using var second = Create(service, "user-1", Features(0, 1));

        second.GetFeatureValue("feature", -1).Should().Be(0, "the user was assigned 0 by the first instance, and the assignment is sticky");
    }

    [Fact]
    public void UpdatingTheAttributesToAnotherUserLoadsThatUsersAssignments()
    {
        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(Stored("user-2", "0"));

        using var gb = Create(service, "user-1", Features(0, 1));
        gb.GetFeatureValue("feature", -1).Should().Be(1, "user-1 has nothing stored and is hashed");

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });

        gb.GetFeatureValue("feature", -1).Should().Be(0, "user-2 was assigned 0 before");
    }

    [Fact]
    public void MergingAnotherIdentifierLoadsThatUsersAssignments()
    {
        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(Stored("user-2", "0"));

        using var gb = Create(service, "user-1", Features(0, 1));

        gb.MergeAttributes(new Dictionary<string, object> { ["id"] = "user-2" });

        gb.GetFeatureValue("feature", -1).Should().Be(0);
    }

    /// <summary>
    /// Which identifiers matter depends on the features, so assignments are loaded again once features arrive.
    /// </summary>
    [Fact]
    public async Task LoadingFeaturesLoadsTheAssignmentsTheyNeed()
    {
        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(Stored("user-1", "0"));

        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(Features(0, 1)));

        using var gb = Create(service, "user-1", new Dictionary<string, Feature>(), repository: repository);

        await gb.LoadFeatures();

        gb.GetFeatureValue("feature", -1).Should().Be(0);
    }

    /// <summary>
    /// Documents handed over on the context may not be in the service. Loading from the service keeps them rather
    /// than replacing them with nothing.
    /// </summary>
    [Fact]
    public void AssignmentsHandedOverOnTheContextAreKept()
    {
        var documents = new Dictionary<string, StickyAssignmentsDocument> { ["id||user-1"] = Stored("user-1", "0") };

        using var gb = Create(new InMemoryStickyBucketService(), "user-1", Features(0, 1), documents);

        gb.GetFeatureValue("feature", -1).Should().Be(0);
    }

    /// <summary>
    /// When the service holds a document for the user that lacks an assignment the instance holds, as when another
    /// instance saved a different experiment, both are kept.
    /// </summary>
    [Fact]
    public void AStoredDocumentIsMergedWithTheOneHeldRatherThanReplacingIt()
    {
        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { ["other__0"] = "1" }));

        var documents = new Dictionary<string, StickyAssignmentsDocument> { ["id||user-1"] = Stored("user-1", "0") };
        var features = Features(0, 1);
        features["other-feature"] = ExperimentFeature("other", 1, 0);

        using var gb = Create(service, "user-1", features, documents);

        // The stored experiment first: saving the held one reads the service again, which would load it anyway.
        gb.GetFeatureValue("other-feature", -1).Should().Be(1, "the stored assignment is loaded alongside the held one, though hashing would pick 0");
        gb.GetFeatureValue("feature", -1).Should().Be(0, "the held assignment survives loading the stored document");
    }

    /// <summary>
    /// An experiment passed to <c>Run</c> directly is in neither the features nor the experiments, so nothing else names
    /// its identifier. It used to be hashed again, and the new choice saved over the stored one.
    /// </summary>
    [Fact]
    public void AnExperimentRunDirectlyFindsItsStoredAssignment()
    {
        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { ["direct__0"] = "0" }));

        using var gb = Create(service, "user-1", new Dictionary<string, Feature>());

        var result = gb.Run(new Experiment
        {
            Key = "direct",
            Variations = new JArray(0, 1),
            Weights = new List<double> { 0, 1 },
            Coverage = 1,
            HashAttribute = "id",
            Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
        });

        result.Value.Value<int>().Should().Be(0, "the user was assigned 0 before, though hashing now picks 1");
        result.StickyBucketUsed.Should().BeTrue();
        service.GetAssignments("id", "user-1").Assignments["direct__0"].Should().Be("0", "the stored choice is not overwritten");
    }

    /// <summary>
    /// A service that cannot be read must not stop an instance being built, nor stop it evaluating.
    /// </summary>
    [Fact]
    public void AServiceThatCannotBeReadDoesNotStopTheInstance()
    {
        var service = Substitute.For<IStickyBucketService>();
        service.GetAllAssignments(Arg.Any<IEnumerable<string>>()).Returns(_ => throw new InvalidOperationException("store unavailable"));

        using var gb = Create(service, "user-1", Features(0, 1));

        gb.GetFeatureValue("feature", -1).Should().Be(1);

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });

        gb.GetFeatureValue("feature", -1).Should().Be(1);
    }
}
