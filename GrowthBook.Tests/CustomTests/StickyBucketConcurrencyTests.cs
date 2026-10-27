using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Evaluation now writes a saved sticky assignment back into the documents it holds, which it did
/// not do before. Evaluations run concurrently, so that dictionary has to survive being written to
/// from several threads: a document lost there is a user who gets re-hashed on the next evaluation,
/// and after a weight or coverage change lands in a different variation.
/// </summary>
public class StickyBucketConcurrencyTests
{
    private const int FeatureCount = 200;

    /// <summary>
    /// Thread-safe on purpose, so a lost document can only have come from the SDK.
    /// </summary>
    private sealed class ConcurrentStickyBucketService : IStickyBucketService
    {
        private readonly ConcurrentDictionary<string, StickyAssignmentsDocument> _documents =
            new ConcurrentDictionary<string, StickyAssignmentsDocument>();

        public IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes) =>
            attributes
                .Select(name => _documents.TryGetValue(name, out var document) ? document : null)
                .Where(document => document != null)
                .ToDictionary(document => document.FormattedAttribute, document => document);

        public StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue) =>
            _documents.TryGetValue($"{attributeName}||{attributeValue}", out var document) ? document : null;

        public void SaveAssignments(StickyAssignmentsDocument document) =>
            _documents[document.FormattedAttribute] = document;
    }

    private static Dictionary<string, Feature> ExperimentPerAttribute() =>
        Enumerable.Range(0, FeatureCount).ToDictionary(
            i => $"feature-{i}",
            i => new Feature
            {
                DefaultValue = JToken.FromObject(false),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Key = $"experiment-{i}",
                        Variations = JArray.FromObject(new[] { false, true }),
                        Weights = new List<double> { 0.5, 0.5 },
                        Coverage = 1.0,
                        // Its own attribute, so every feature produces its own sticky document.
                        HashAttribute = $"attr-{i}",
                        BucketVersion = 1
                    }
                }
            });

    [Fact]
    public void EvaluatingConcurrentlyKeepsEverySavedStickyDocument()
    {
        var features = ExperimentPerAttribute();

        var attributes = new JObject();

        for (var i = 0; i < FeatureCount; i++)
        {
            attributes[$"attr-{i}"] = $"user-{i}";
        }

        attributes["id"] = "user-0";

        // Deliberately not disposed. Concurrent evaluation also writes the instance's own assignment
        // dictionary, which is a plain Dictionary on this branch, so Dispose clearing it throws
        // IndexOutOfRangeException out of a corrupted bucket array. That is a separate defect on
        // main, fixed by the ConcurrentDictionary in the instance-lifetime branch; this test is
        // about the sticky documents and should not fail for it.
        var growthBook = new GrowthBookSdk.GrowthBook(new Context
        {
            Enabled = true,
            Attributes = attributes,
            Features = features,
            StickyBucketService = new ConcurrentStickyBucketService(),
            LoggerFactory = NullLoggerFactory.Instance
        });

        Action evaluateEverythingInParallel = () =>
        {
            for (var round = 0; round < 50; round++)
            {
                System.Threading.Tasks.Parallel.ForEach(
                    features.Keys,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 },
                    key => growthBook.EvalFeature(key));
            }
        };

        evaluateEverythingInParallel.Should().NotThrow();

        var documents = StickyDocuments(growthBook);

        documents.Should().HaveCount(FeatureCount,
            "every evaluation saved its assignment, and none of them may be lost to a concurrent write");
    }

    /// <summary>
    /// The documents the instance holds, which is what the write goes into. The service is
    /// thread-safe here by design, so asking it instead would pass whatever the SDK did.
    /// </summary>
    private static IDictionary<string, StickyAssignmentsDocument> StickyDocuments(GrowthBookSdk.GrowthBook growthBook) =>
        (IDictionary<string, StickyAssignmentsDocument>)typeof(GrowthBookSdk.GrowthBook)
            .GetField("_stickyBucketAssignmentDocs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(growthBook);
}
