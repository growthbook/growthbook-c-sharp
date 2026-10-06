using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class RuleIdTests : UnitTest
{
    [Fact]
    public void RuleIdIsEmptyWhenSourceIsDefaultValue()
    {
        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                ["test-feature"] = new Feature { DefaultValue = true }
            }
        };

        var growthBook = new GrowthBook(context);
        var result = growthBook.EvalFeature("test-feature");

        result.Source.Should().Be(FeatureResult.SourceId.DefaultValue);
        result.RuleId.Should().Be(string.Empty, "because no rule matched, so there is no rule id to report");
    }

    [Fact]
    public void RuleIdIsEmptyWhenFeatureIsUnknown()
    {
        var growthBook = new GrowthBook(new Context());
        var result = growthBook.EvalFeature("does-not-exist");

        result.Source.Should().Be(FeatureResult.SourceId.UnknownFeature);
        result.RuleId.Should().Be(string.Empty);
    }

    [Fact]
    public void RuleIdIsPopulatedWhenAForceRuleMatches()
    {
        const string ExpectedRuleId = "rule-abc-123";

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                ["test-feature"] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule>
                    {
                        new FeatureRule { Id = ExpectedRuleId, Force = true }
                    }
                }
            }
        };

        var growthBook = new GrowthBook(context);
        var result = growthBook.EvalFeature("test-feature");

        result.Source.Should().Be(FeatureResult.SourceId.Force);
        result.RuleId.Should().Be(ExpectedRuleId);
    }

    [Fact]
    public void RuleIdIsEmptyWhenAForceRuleHasNoIdSet()
    {
        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                ["test-feature"] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule> { new FeatureRule { Force = true } }
                }
            }
        };

        var growthBook = new GrowthBook(context);
        var result = growthBook.EvalFeature("test-feature");

        result.Source.Should().Be(FeatureResult.SourceId.Force);
        result.RuleId.Should().Be(string.Empty, "because the rule never had an Id assigned");
    }

    [Fact]
    public void RuleIdIsPopulatedWhenAnExperimentRuleMatches()
    {
        const string ExpectedRuleId = "exp-rule-xyz";

        var context = new Context
        {
            Attributes = Newtonsoft.Json.Linq.JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature>
            {
                ["test-feature"] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule>
                    {
                        new FeatureRule
                        {
                            Id = ExpectedRuleId,
                            Variations = new Newtonsoft.Json.Linq.JArray(false, true),
                            Coverage = 1d
                        }
                    }
                }
            }
        };

        var growthBook = new GrowthBook(context);
        var result = growthBook.EvalFeature("test-feature");

        result.Source.Should().Be(FeatureResult.SourceId.Experiment);
        result.RuleId.Should().Be(ExpectedRuleId);
    }
}
