using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <see cref="FeatureResult.RuleId"/> names the rule that decided the value, as <c>ruleId</c> in the JavaScript SDK:
/// set for a forced rule and for an experiment rule, empty for every other source.
/// </summary>
public class RuleIdTests
{
    private static GrowthBook Create(Dictionary<string, Feature> features, IDictionary<string, JToken> forced = null) =>
        new GrowthBook(new Context
        {
            Enabled = true,
            Attributes = new JObject { ["id"] = "u1", ["plan"] = "pro" },
            Features = features,
            ForcedFeatures = forced,
            LoggerFactory = NullLoggerFactory.Instance
        });

    private static Dictionary<string, Feature> Features() => new Dictionary<string, Feature>
    {
        ["forced-by-rule"] = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule>
            {
                new FeatureRule { Id = "rule-skipped", Condition = JObject.Parse(@"{""plan"": ""free""}"), Force = "free" },
                new FeatureRule { Id = "rule-pro", Condition = JObject.Parse(@"{""plan"": ""pro""}"), Force = "pro" }
            }
        },
        ["experiment"] = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule>
            {
                new FeatureRule { Id = "rule-exp", Key = "exp", Variations = new JArray("a", "b"), Coverage = 1 }
            }
        },
        ["no-rule-matches"] = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule>
            {
                new FeatureRule { Id = "rule-free", Condition = JObject.Parse(@"{""plan"": ""free""}"), Force = "free" }
            }
        },
        ["rule-without-id"] = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule> { new FeatureRule { Force = "forced" } }
        }
    };

    [Fact]
    public void AForcedRuleReportsItsId()
    {
        using var gb = Create(Features());

        var result = gb.EvalFeature("forced-by-rule");

        result.Source.Should().Be(FeatureResult.SourceId.Force);
        result.RuleId.Should().Be("rule-pro", "the first rule did not match, the second decided the value");
    }

    [Fact]
    public void AnExperimentRuleReportsItsId()
    {
        using var gb = Create(Features());

        var result = gb.EvalFeature("experiment");

        result.Source.Should().Be(FeatureResult.SourceId.Experiment);
        result.RuleId.Should().Be("rule-exp");
    }

    [Theory]
    [InlineData("no-rule-matches", FeatureResult.SourceId.DefaultValue)]
    [InlineData("rule-without-id", FeatureResult.SourceId.Force)]
    [InlineData("not-in-payload", FeatureResult.SourceId.UnknownFeature)]
    public void TheIdIsEmptyWhenNoRuleWithAnIdDecidedTheValue(string feature, string expectedSource)
    {
        using var gb = Create(Features());

        var result = gb.EvalFeature(feature);

        result.Source.Should().Be(expectedSource);
        result.RuleId.Should().BeEmpty();
    }

    [Fact]
    public void AForcedFeatureReportsNoRule()
    {
        using var gb = Create(Features(), new Dictionary<string, JToken> { ["forced-by-rule"] = "override" });

        var result = gb.EvalFeature("forced-by-rule");

        result.Source.Should().Be(FeatureResult.SourceId.Override);
        result.RuleId.Should().BeEmpty("no rule was evaluated");
    }

    [Fact]
    public void ResultsFromDifferentRulesAreNotEqual()
    {
        var first = new FeatureResult { Value = "v", Source = FeatureResult.SourceId.Force, RuleId = "rule-1" };
        var second = new FeatureResult { Value = "v", Source = FeatureResult.SourceId.Force, RuleId = "rule-2" };

        first.Should().NotBe(second);
    }
}
