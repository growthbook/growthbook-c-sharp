using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// An experiment rule's condition is checked inside the experiment, after the sticky bucket lookup,
/// so a user who already has a sticky assignment keeps it after they stop matching the condition.
/// Only force rules check their condition up front. This is how the JS SDK evaluates features.
/// </summary>
public class StickyBucketRuleConditionTests
{
    private const string FeatureId = "checkout-redesign";
    private const string ExperimentKey = "checkout-exp";
    private const string UserId = "user-1";

    private static readonly JObject UsUser = new JObject { ["id"] = UserId, ["country"] = "US" };

    private static JObject CountryIn(params string[] countries) =>
        new JObject { ["country"] = new JObject { ["$in"] = new JArray(countries) } };

    /// <summary>
    /// Hashing always picks 0, so a 1 can only have come from a sticky assignment or a forced variation.
    /// </summary>
    private static FeatureRule ExperimentRule(JObject condition, IList<double> weights = null) => new FeatureRule
    {
        Key = ExperimentKey,
        Variations = new JArray(0, 1),
        Weights = weights ?? new List<double> { 1, 0 },
        Coverage = 1,
        HashAttribute = "id",
        Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } },
        Condition = condition
    };

    private static Feature FeatureWith(params FeatureRule[] rules) => new Feature
    {
        DefaultValue = JToken.FromObject(-1),
        Rules = new List<FeatureRule>(rules)
    };

    private static Dictionary<string, StickyAssignmentsDocument> StickyAssignmentToVariation1() =>
        new Dictionary<string, StickyAssignmentsDocument>
        {
            [$"id||{UserId}"] = new StickyAssignmentsDocument("id", UserId,
                new Dictionary<string, string> { [$"{ExperimentKey}__0"] = "1" })
        };

    private static GrowthBookSdk.GrowthBook CreateGrowthBook(
        IDictionary<string, Feature> features,
        bool withStickyBucketing = true,
        Dictionary<string, StickyAssignmentsDocument> stickyDocuments = null,
        IDictionary<string, int> forcedVariations = null) =>
        new GrowthBookSdk.GrowthBook(new Context
        {
            Attributes = (JObject)UsUser.DeepClone(),
            Features = features,
            StickyBucketService = withStickyBucketing ? new InMemoryStickyBucketService() : null,
            StickyBucketAssignmentDocs = stickyDocuments,
            ForcedVariations = forcedVariations ?? new Dictionary<string, int>(),
            LoggerFactory = NullLoggerFactory.Instance
        });

    /// <summary>
    /// The repro from growthbook-python#161: the user is bucketed while the condition is
    /// [US, CA], then the condition is narrowed to [CA] mid-test.
    /// </summary>
    [Fact]
    public void StickyBucketWinsOverExperimentRuleCondition()
    {
        // Everyone hashes into 1 here, so the first evaluation saves a sticky assignment to 1.
        var growthBook = CreateGrowthBook(new Dictionary<string, Feature>
        {
            [FeatureId] = FeatureWith(ExperimentRule(CountryIn("US", "CA"), weights: new List<double> { 0, 1 }))
        });

        var first = growthBook.EvalFeature(FeatureId);
        first.Value.ToObject<int>().Should().Be(1);
        first.Source.Should().Be(FeatureResult.SourceId.Experiment);

        growthBook.Features = new Dictionary<string, Feature>
        {
            [FeatureId] = FeatureWith(ExperimentRule(CountryIn("CA")))
        };

        var second = growthBook.EvalFeature(FeatureId);

        second.Value.ToObject<int>().Should().Be(1, "the user keeps their sticky variation");
        second.Source.Should().Be(FeatureResult.SourceId.Experiment);
        second.ExperimentResult.StickyBucketUsed.Should().BeTrue();
    }

    [Fact]
    public void StickyBucketWinsOverLaterForceRule()
    {
        var growthBook = CreateGrowthBook(
            new Dictionary<string, Feature>
            {
                [FeatureId] = FeatureWith(
                    ExperimentRule(CountryIn("CA")),
                    new FeatureRule { Force = JToken.FromObject(99) })
            },
            stickyDocuments: StickyAssignmentToVariation1());

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(1, "the sticky experiment rule comes first and keeps the user");
        result.Source.Should().Be(FeatureResult.SourceId.Experiment);
        result.ExperimentResult.StickyBucketUsed.Should().BeTrue();
    }

    [Fact]
    public void ForcedVariationWinsOverExperimentRuleCondition()
    {
        var growthBook = CreateGrowthBook(
            new Dictionary<string, Feature> { [FeatureId] = FeatureWith(ExperimentRule(CountryIn("CA"))) },
            withStickyBucketing: false,
            forcedVariations: new Dictionary<string, int> { [ExperimentKey] = 1 });

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(1, "forced variations are checked before the condition, as in JS");
        result.Source.Should().Be(FeatureResult.SourceId.Experiment);
    }

    [Fact]
    public void ExperimentRuleConditionAppliesWithoutStickyBucket()
    {
        var growthBook = CreateGrowthBook(new Dictionary<string, Feature>
        {
            [FeatureId] = FeatureWith(
                ExperimentRule(CountryIn("CA")),
                new FeatureRule { Force = JToken.FromObject(99) })
        });

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(99, "with no sticky assignment the experiment rule is skipped");
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Theory]
    [InlineData("US", 99, FeatureResult.SourceId.Force)]
    [InlineData("CA", -1, FeatureResult.SourceId.DefaultValue)]
    public void ForceRuleConditionStillApplies(string ruleCountry, int expectedValue, string expectedSource)
    {
        var growthBook = CreateGrowthBook(
            new Dictionary<string, Feature>
            {
                [FeatureId] = FeatureWith(new FeatureRule { Force = JToken.FromObject(99), Condition = CountryIn(ruleCountry) })
            },
            stickyDocuments: StickyAssignmentToVariation1());

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(expectedValue);
        result.Source.Should().Be(expectedSource);
    }

    [Fact]
    public void FiltersStillApplyWithStickyBucket()
    {
        var rule = ExperimentRule(CountryIn("US"));
        // No ranges, so every user is filtered out.
        rule.Filters = new List<Filter> { new Filter { Seed = "filter-seed", Ranges = new BucketRange[0] } };

        var growthBook = CreateGrowthBook(
            new Dictionary<string, Feature> { [FeatureId] = FeatureWith(rule) },
            stickyDocuments: StickyAssignmentToVariation1());

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(-1, "rule filters are checked before the experiment runs");
        result.Source.Should().Be(FeatureResult.SourceId.DefaultValue);
    }

    [Fact]
    public void PrerequisitesStillApplyWithStickyBucket()
    {
        var rule = ExperimentRule(CountryIn("US"));
        rule.ParentConditions = new List<ParentCondition>
        {
            new ParentCondition { Id = "parent-flag", Condition = new JObject { ["value"] = true } }
        };

        var growthBook = CreateGrowthBook(
            new Dictionary<string, Feature>
            {
                ["parent-flag"] = new Feature { DefaultValue = JToken.FromObject(false) },
                [FeatureId] = FeatureWith(rule)
            },
            stickyDocuments: StickyAssignmentToVariation1());

        var result = growthBook.EvalFeature(FeatureId);

        result.Value.ToObject<int>().Should().Be(-1, "prerequisites are checked before the experiment runs");
        result.Source.Should().Be(FeatureResult.SourceId.DefaultValue);
    }

    /// <summary>
    /// The experiment now runs for a user who fails its condition, so the assignment is recorded as not
    /// in the experiment, as the JS SDK records it. No exposure is tracked for it.
    /// </summary>
    [Fact]
    public void FailedExperimentRuleConditionIsRecordedButNotTracked()
    {
        var tracked = new List<string>();
        var notified = new List<ExperimentResult>();

        var growthBook = new GrowthBookSdk.GrowthBook(new Context
        {
            Attributes = (JObject)UsUser.DeepClone(),
            Features = new Dictionary<string, Feature> { [FeatureId] = FeatureWith(ExperimentRule(CountryIn("CA"))) },
            TrackingCallback = (experiment, _) => tracked.Add(experiment.Key),
            LoggerFactory = NullLoggerFactory.Instance
        });
        using var subscription = growthBook.Subscribe((_, result) => notified.Add(result));

        growthBook.EvalFeature(FeatureId).Source.Should().Be(FeatureResult.SourceId.DefaultValue);

        growthBook.GetAllResults().Should().ContainKey(ExperimentKey)
            .WhoseValue.Result.InExperiment.Should().BeFalse();
        notified.Should().ContainSingle().Which.InExperiment.Should().BeFalse();
        tracked.Should().BeEmpty();
    }
}
