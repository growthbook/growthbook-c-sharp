using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.Providers;
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

    /// <summary>
    /// The multi-user client passes a request's documents straight through, and a request can check flags in
    /// parallel. A read overlapping a write on a plain dictionary can miss a saved choice or throw, so reads take
    /// the lock writes take.
    /// </summary>
    [Fact]
    public void ReadingTheStickyDocumentsNeverOverlapsAWrite()
    {
        const int ExperimentCount = 50;

        var conditions = new ConditionEvaluationProvider(NullLogger<ConditionEvaluationProvider>.Instance);
        var features = new FeatureEvaluationProvider(NullLogger.Instance, conditions);
        var experiments = new ExperimentEvaluationProvider(NullLogger.Instance, conditions);

        features.ExperimentEvaluator = experiments;
        experiments.FeatureEvaluator = features;

        var attributes = new JObject();

        var global = new GlobalContext
        {
            StickyBucketService = new InMemoryStickyBucketService(),
            Features = Enumerable.Range(0, ExperimentCount).ToDictionary(
                i => $"feature-{i}",
                i =>
                {
                    attributes[$"attr-{i}"] = "user-1";

                    var feature = StickyExperiment($"exp-{i}");
                    feature.Rules[0].HashAttribute = $"attr-{i}";
                    feature.Rules[0].Weights = new List<double> { 0.5, 0.5 };

                    return feature;
                })
        };

        var documents = new OverlapDetectingDictionary();
        var user = new UserContext { Attributes = attributes, StickyBucketAssignmentDocs = documents };

        // Every experiment hashes on its own attribute, so each first evaluation adds a document while the others read.
        Parallel.ForEach(global.Features.Keys.Concat(global.Features.Keys), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            key => features.EvaluateFeature(key, new EvaluationContext(global, user)));

        documents.Overlaps.Should().Be(0, "no read of the documents may happen while one of them is being written");
        documents.Count.Should().Be(ExperimentCount);
    }

    /// <summary>
    /// Counts reads that land while a write is in progress, holding each write open briefly so an unguarded
    /// read has a window to fall into.
    /// </summary>
    private sealed class OverlapDetectingDictionary : IDictionary<string, StickyAssignmentsDocument>
    {
        private readonly Dictionary<string, StickyAssignmentsDocument> _inner = new Dictionary<string, StickyAssignmentsDocument>();
        private int _writing;
        private int _overlaps;

        public int Overlaps => Volatile.Read(ref _overlaps);

        public StickyAssignmentsDocument this[string key]
        {
            get { Read(); return _inner[key]; }
            set
            {
                Interlocked.Exchange(ref _writing, 1);
                Thread.Sleep(1);
                _inner[key] = value;
                Interlocked.Exchange(ref _writing, 0);
            }
        }

        public bool TryGetValue(string key, out StickyAssignmentsDocument value)
        {
            Read();
            return _inner.TryGetValue(key, out value);
        }

        public bool ContainsKey(string key)
        {
            Read();
            return _inner.ContainsKey(key);
        }

        private void Read()
        {
            if (Volatile.Read(ref _writing) == 1)
            {
                Interlocked.Increment(ref _overlaps);
            }
        }

        public ICollection<string> Keys => _inner.Keys;
        public ICollection<StickyAssignmentsDocument> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public void Add(string key, StickyAssignmentsDocument value) => this[key] = value;
        public void Add(KeyValuePair<string, StickyAssignmentsDocument> item) => this[item.Key] = item.Value;
        public bool Remove(string key) => _inner.Remove(key);
        public bool Remove(KeyValuePair<string, StickyAssignmentsDocument> item) => _inner.Remove(item.Key);
        public void Clear() => _inner.Clear();
        public bool Contains(KeyValuePair<string, StickyAssignmentsDocument> item) => ((ICollection<KeyValuePair<string, StickyAssignmentsDocument>>)_inner).Contains(item);
        public void CopyTo(KeyValuePair<string, StickyAssignmentsDocument>[] array, int arrayIndex) => ((ICollection<KeyValuePair<string, StickyAssignmentsDocument>>)_inner).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, StickyAssignmentsDocument>> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static IDictionary<string, StickyAssignmentsDocument> StickyDocuments(GrowthBookSdk.GrowthBook growthBook) =>
        (IDictionary<string, StickyAssignmentsDocument>)typeof(GrowthBookSdk.GrowthBook)
            .GetField("_stickyBucketAssignmentDocs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(growthBook);
}
