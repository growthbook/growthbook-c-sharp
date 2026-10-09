using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class OnFeatureUsageTests : UnitTest
{
    private sealed class RecordedUsage
    {
        public string Key { get; set; }
        public FeatureResult Result { get; set; }
    }

    [Fact]
    public void OnFeatureUsageDedupesRepeatedEvaluationsWithTheSameValue()
    {
        const string FeatureName = "checkout-flow";

        var usages = new List<RecordedUsage>();

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [FeatureName] = new Feature { DefaultValue = false }
            },
            OnFeatureUsage = (key, result) => usages.Add(new RecordedUsage { Key = key, Result = result })
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(FeatureName);
        growthBook.EvalFeature(FeatureName);
        growthBook.EvalFeature(FeatureName);

        usages.Should().HaveCount(1, "because the callback is deduped per key and only fires again when the resolved value changes");
        usages[0].Key.Should().Be(FeatureName);
        usages[0].Result.Source.Should().Be(FeatureResult.SourceId.DefaultValue);
    }

    [Fact]
    public void OnFeatureUsageFiresAgainWhenTheResolvedValueChanges()
    {
        const string FeatureName = "checkout-flow";

        var usages = new List<RecordedUsage>();

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [FeatureName] = new Feature { DefaultValue = false }
            },
            OnFeatureUsage = (key, result) => usages.Add(new RecordedUsage { Key = key, Result = result })
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(FeatureName);
        growthBook.EvalFeature(FeatureName);

        growthBook.Features[FeatureName] = new Feature { DefaultValue = true };

        growthBook.EvalFeature(FeatureName);

        usages.Should().HaveCount(2, "because the second, differing value must fire again despite the earlier dedup");
        usages[0].Result.On.Should().BeFalse();
        usages[1].Result.On.Should().BeTrue();
    }

    [Fact]
    public void OnFeatureUsageReceivesTheCorrectSourceAndValuePerEvaluationPath()
    {
        const string ForcedFeature = "forced-feature";
        const string UnknownFeature = "unknown-feature";

        var usages = new List<RecordedUsage>();

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [ForcedFeature] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule> { new() { Force = true } }
                }
            },
            OnFeatureUsage = (key, result) => usages.Add(new RecordedUsage { Key = key, Result = result })
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(ForcedFeature);
        growthBook.EvalFeature(UnknownFeature);

        usages.Should().HaveCount(2);

        var forced = usages.Find(u => u.Key == ForcedFeature);
        forced.Should().NotBeNull();
        forced.Result.Source.Should().Be(FeatureResult.SourceId.Force);
        forced.Result.On.Should().BeTrue();

        var unknown = usages.Find(u => u.Key == UnknownFeature);
        unknown.Should().NotBeNull();
        unknown.Result.Source.Should().Be(FeatureResult.SourceId.UnknownFeature);
    }

    [Fact]
    public void OnFeatureUsageFiresForPrerequisiteFeaturesEvaluatedDuringAParentLookup()
    {
        const string ParentFeature = "parent-feature";
        const string ChildFeature = "child-feature";

        var usages = new List<string>();

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [ParentFeature] = new Feature { DefaultValue = true },
                [ChildFeature] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule>
                    {
                        new()
                        {
                            Force = true,
                            ParentConditions = new List<ParentCondition>
                            {
                                new() { Id = ParentFeature, Condition = new JObject { ["value"] = true } }
                            }
                        }
                    }
                }
            },
            OnFeatureUsage = (key, _) => usages.Add(key)
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(ChildFeature);

        usages.Should().Contain(ChildFeature, "because the feature that was directly requested must be reported");
        usages.Should().Contain(ParentFeature, "because the prerequisite feature is evaluated recursively and must be reported too");
    }

    [Fact]
    public void ExceptionsThrownByOnFeatureUsageAreCaughtAndDoNotBreakEvaluation()
    {
        const string FeatureName = "checkout-flow";

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [FeatureName] = new Feature { DefaultValue = true }
            },
            OnFeatureUsage = (_, _) => throw new InvalidOperationException("boom")
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Should().NotBeNull();
        result.On.Should().BeTrue("because a failing callback must not prevent the feature result from being returned");
    }

    [Fact]
    public void OnFeatureUsageDoesNotFireForStandaloneExperimentRuns()
    {
        var usages = new List<string>();

        var context = new Context
        {
            OnFeatureUsage = (key, _) => usages.Add(key)
        };

        var growthBook = new GrowthBook(context);

        growthBook.Run(new Experiment
        {
            Key = "standalone-experiment",
            Variations = new JArray("red", "blue")
        });

        usages.Should().BeEmpty("because OnFeatureUsage only fires from feature evaluation, not from Run()");
    }

    /// <summary>
    /// Two threads evaluating the same feature could both read the old value before either wrote it,
    /// so both reported the same usage. The dictionary makes each operation safe; it is the
    /// check-and-update sequence that has to be atomic.
    /// </summary>
    [Fact]
    public void ParallelEvaluationsOfTheSameValueReportUsageOnce()
    {
        const string FeatureName = "checkout-flow";

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var usages = 0;

            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                Features = new Dictionary<string, Feature>
                {
                    [FeatureName] = new Feature { DefaultValue = false }
                },
                OnFeatureUsage = (key, result) => Interlocked.Increment(ref usages)
            });

            var ready = new CountdownEvent(8);

            var racers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                ready.Signal();
                ready.Wait(TimeSpan.FromSeconds(5));

                growthBook.EvalFeature(FeatureName);
            })).ToArray();

            Task.WaitAll(racers, TimeSpan.FromSeconds(10));

            usages.Should().Be(1,
                "the value never changed, so however many threads evaluated it there is one usage to report");
        }
    }

    [Fact]
    public void AChangedValueIsStillReported()
    {
        const string FeatureName = "checkout-flow";

        var usages = 0;

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>
            {
                [FeatureName] = new Feature { DefaultValue = false }
            },
            OnFeatureUsage = (key, result) => Interlocked.Increment(ref usages)
        });

        growthBook.EvalFeature(FeatureName);
        growthBook.Features = new Dictionary<string, Feature>
        {
            [FeatureName] = new Feature { DefaultValue = true }
        };
        growthBook.EvalFeature(FeatureName);

        usages.Should().Be(2, "deduping is per value, not per feature - a changed value is new usage");
    }
}
