using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers <see cref="GrowthBook.GetAllResults"/> handing out a snapshot rather than the dictionary the
/// instance evaluates against. It used to return the internal collection directly, so a caller reading
/// the results could clear or rewrite the instance's own assignment state.
/// </summary>
public class GetAllResultsSnapshotTests : UnitTest
{
    private static Feature ExperimentFeature(string experimentKey) => new Feature
    {
        DefaultValue = false,
        Rules = new List<FeatureRule>
        {
            new FeatureRule
            {
                Key = experimentKey,
                Variations = new JArray(false, true),
                Coverage = 1d,
                Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
            }
        }
    };

    private static GrowthBook NewInstance() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature>
        {
            ["first"] = ExperimentFeature("first-experiment"),
            ["second"] = ExperimentFeature("second-experiment")
        }
    });

    [Fact]
    public void ItIsEmptyRatherThanNullBeforeAnythingIsEvaluated()
    {
        using var growthBook = NewInstance();

        growthBook.GetAllResults().Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ClearingTheReturnedMapLeavesTheInstanceIntact()
    {
        using var growthBook = NewInstance();

        growthBook.EvalFeature("first");

        growthBook.GetAllResults().Clear();

        growthBook.GetAllResults().Should().ContainKey("first-experiment",
            "because the caller was handed a copy - clearing it must not wipe the instance's own state");
    }

    [Fact]
    public void WritingIntoTheReturnedMapDoesNotReachTheInstance()
    {
        using var growthBook = NewInstance();

        growthBook.EvalFeature("first");

        growthBook.GetAllResults()["injected"] = new ExperimentAssignment();

        growthBook.GetAllResults().Should().NotContainKey("injected");
    }

    [Fact]
    public void AnEarlierSnapshotDoesNotSeeLaterEvaluations()
    {
        using var growthBook = NewInstance();

        growthBook.EvalFeature("first");

        var snapshot = growthBook.GetAllResults();

        snapshot.Should().HaveCount(1);

        growthBook.EvalFeature("second");

        snapshot.Should().HaveCount(1, "a snapshot is a point in time, not a live view");
        growthBook.GetAllResults().Should().HaveCount(2, "while a fresh call reflects both evaluations");
    }

    [Fact]
    public void TheSnapshotStillCarriesTheAssignments()
    {
        using var growthBook = NewInstance();

        growthBook.EvalFeature("first");

        var results = growthBook.GetAllResults();

        results.Should().ContainKey("first-experiment");
        results["first-experiment"].Experiment.Key.Should().Be("first-experiment");
        results["first-experiment"].Result.InExperiment.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public async Task ReadingWhileEvaluationIsRunningDoesNotThrow()
    {
        // Copying a Dictionary while another thread writes to it throws. The read has to be serialised
        // against the writes, not just defensive.
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>()
        });

        for (var i = 0; i < 200; i++)
        {
            growthBook.Features[$"flag-{i}"] = ExperimentFeature($"experiment-{i}");
        }

        const int ReadsRequired = 2000;
        const int WritesRequired = 400;

        var reads = 0;
        var writes = 0;

        // Both workers run until both quotas are met, so neither can finish its share while the
        // other is still starting - a reader that outruns the writer overlaps with nothing.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        bool KeepGoing() =>
            DateTime.UtcNow < deadline &&
            (Volatile.Read(ref reads) < ReadsRequired || Volatile.Read(ref writes) < WritesRequired);

        var ready = new CountdownEvent(2);

        var evaluating = Task.Run(() =>
        {
            ready.Signal();
            ready.Wait(TimeSpan.FromSeconds(5));

            // Each round introduces a new experiment key so the assignment dictionary keeps growing:
            // it is the resize, not the value write, that tears a concurrent copy, and a fixed set
            // of keys stops resizing after the first round.
            var next = 200;

            while (KeepGoing())
            {
                var key = $"flag-{next}";

                growthBook.Features[key] = ExperimentFeature($"experiment-{next}");
                next++;

                growthBook.EvalFeature(key);
                Interlocked.Increment(ref writes);
            }
        });

        var reading = Task.Run(() =>
        {
            ready.Signal();
            ready.Wait(TimeSpan.FromSeconds(5));

            while (KeepGoing())
            {
                _ = growthBook.GetAllResults().Count;
                Interlocked.Increment(ref reads);
            }
        });

        await Task.WhenAll(evaluating, reading);

        // Without these the test passes having overlapped nothing at all, so removing the write-side
        // lock would go unnoticed.
        reads.Should().BeGreaterOrEqualTo(ReadsRequired, "the reads have to have actually happened");
        writes.Should().BeGreaterOrEqualTo(WritesRequired, "and the writes have to have been running while they did");
    }
}
