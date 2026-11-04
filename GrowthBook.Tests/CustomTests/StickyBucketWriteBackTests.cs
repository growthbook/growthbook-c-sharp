using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// A new sticky assignment is merged into the user's document and written back to the documents the
/// instance holds. Whatever that document already had must survive the write.
/// </summary>
public class StickyBucketWriteBackTests
{
    private static Feature StickyExperiment(string key) => new Feature
    {
        DefaultValue = JToken.FromObject(-1),
        Rules = new List<FeatureRule>
        {
            new FeatureRule
            {
                Key = key,
                Variations = new JArray(0, 1),
                // Hashing always picks 0, so a 1 can only have come from a saved assignment.
                Weights = new List<double> { 1, 0 },
                Coverage = 1,
                HashAttribute = "id",
                Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
            }
        }
    };

    /// <summary>
    /// Assignments handed over on the context, with a service that does not hold them, as when they were
    /// loaded from elsewhere. Saving another experiment must not replace them.
    /// </summary>
    [Fact]
    public void APreloadedAssignmentSurvivesSavingAnotherExperiment()
    {
        var growthBook = new GrowthBookSdk.GrowthBook(new Context
        {
            Attributes = new JObject { ["id"] = "user-1" },
            Features = new Dictionary<string, Feature>
            {
                ["feature-a"] = StickyExperiment("exp-a"),
                ["feature-b"] = StickyExperiment("exp-b")
            },
            StickyBucketService = new InMemoryStickyBucketService(),
            StickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument>
            {
                ["id||user-1"] = new StickyAssignmentsDocument("id", "user-1", new Dictionary<string, string> { ["exp-a__0"] = "1" })
            },
            LoggerFactory = NullLoggerFactory.Instance
        });

        growthBook.GetFeatureValue("feature-b", -1).Should().Be(0);

        growthBook.GetFeatureValue("feature-a", -1).Should().Be(1,
            "the user was assigned to 1 before, and saving exp-b must not have dropped that");
    }

    /// <summary>
    /// Every experiment here hashes on the same attribute, so every assignment goes into one document.
    /// Evaluated at once, each save reads that document, adds its own assignment and writes it back; two
    /// of them interleaving would each write a document missing the other's.
    /// </summary>
    [Fact]
    public void ConcurrentAssignmentsForOneUserAreAllKept()
    {
        const int ExperimentCount = 100;

        var features = Enumerable.Range(0, ExperimentCount)
            .ToDictionary(i => $"feature-{i}", i => StickyExperiment($"exp-{i}"));

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var service = new InMemoryStickyBucketService();

            // Not disposed, as in StickyBucketConcurrencyTests: disposing clears the instance's assignment
            // dictionary, which concurrent evaluation can corrupt on this branch.
            var growthBook = new GrowthBookSdk.GrowthBook(new Context
            {
                Attributes = new JObject { ["id"] = "user-1" },
                Features = features,
                StickyBucketService = service,
                LoggerFactory = NullLoggerFactory.Instance
            });

            Parallel.ForEach(features.Keys, new ParallelOptions { MaxDegreeOfParallelism = 16 }, key => growthBook.EvalFeature(key));

            service.GetAssignments("id", "user-1").Assignments.Should().HaveCount(ExperimentCount,
                "every experiment saved its assignment, and none may be lost to a concurrent save");

            StickyDocuments(growthBook)["id||user-1"].Assignments.Should().HaveCount(ExperimentCount,
                "the instance's own copy has to hold them all too");
        }
    }

    private static IDictionary<string, StickyAssignmentsDocument> StickyDocuments(GrowthBookSdk.GrowthBook growthBook) =>
        (IDictionary<string, StickyAssignmentsDocument>)typeof(GrowthBookSdk.GrowthBook)
            .GetField("_stickyBucketAssignmentDocs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(growthBook);
}
