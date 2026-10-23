using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class GetAllResultsTests : UnitTest
{
    private static FeatureRule CreateExperimentRule() => new FeatureRule
    {
        Variations = new JArray(false, true),
        Coverage = 1d
    };

    [Fact]
    public void GetAllResultsReturnsASnapshotNotTheLiveDictionary()
    {
        const string FeatureName = "test-feature";

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>
            {
                [FeatureName] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule> { CreateExperimentRule() }
                }
            }
        };

        var growthBook = new GrowthBook(context);
        growthBook.EvalFeature(FeatureName);

        var results = growthBook.GetAllResults();
        results.Should().ContainKey(FeatureName);

        // Mutating the returned map must not reach into the SDK's own assignment state.
        results.Clear();
        results["injected-key"] = new ExperimentAssignment();

        var freshResults = growthBook.GetAllResults();
        freshResults.Should().ContainKey(FeatureName, "because the caller only received a copy");
        freshResults.Should().NotContainKey("injected-key");
    }

    [Fact]
    public void GetAllResultsCanBeEnumeratedWhileEvaluationKeepsWriting()
    {
        // With a plain Dictionary behind GetAllResults this would throw
        // "Collection was modified" or corrupt the dictionary outright.
        var features = new Dictionary<string, Feature>();

        for (var i = 0; i < 50; i++)
        {
            features[$"feature-{i}"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule> { CreateExperimentRule() }
            };
        }

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = features
        };

        var growthBook = new GrowthBook(context);

        var reads = 0;

        Action evaluateAndEnumerateConcurrently = () =>
        {
            var writing = true;

            using var readerIsRunning = new ManualResetEventSlim(false);

            // Reads until the writer is finished rather than for a fixed count, and the writer waits
            // for this loop before it starts. Without both, the writer can finish first, leaving the
            // reader to see writing already false and take no snapshot at all - and taking the
            // snapshot is the only place a non-concurrent store would throw.
            var reader = Task.Run(() =>
            {
                readerIsRunning.Set();

                while (Volatile.Read(ref writing))
                {
                    foreach (var entry in growthBook.GetAllResults())
                    {
                        _ = entry.Key;
                    }

                    Interlocked.Increment(ref reads);
                }
            });

            var writer = Task.Run(() =>
            {
                readerIsRunning.Wait(TimeSpan.FromSeconds(10));

                try
                {
                    for (var pass = 0; pass < 200; pass++)
                    {
                        foreach (var key in features.Keys)
                        {
                            growthBook.EvalFeature(key);
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref writing, false);
                }
            });

            Task.WaitAll(writer, reader);
        };

        evaluateAndEnumerateConcurrently.Should().NotThrow("because assignments are stored in a concurrent dictionary and GetAllResults hands back a snapshot");

        reads.Should().BeGreaterThan(0, "a run where the reader never took a snapshot proves nothing");
        growthBook.GetAllResults().Should().NotBeEmpty("and the writer really did record assignments");
    }
}
