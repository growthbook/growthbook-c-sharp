using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// A condition that can't be evaluated, such as a malformed one, fails that condition only. The rest of the
/// feature or experiment is still evaluated, as in the JS SDK.
/// </summary>
public class MalformedConditionTests : UnitTest
{
    private const string FeatureName = "malformed-condition";

    // An $or entry must be an object; a null entry can't be evaluated
    private static readonly JObject MalformedCondition = JObject.Parse(@"{ ""$or"": [null] }");

    [Fact]
    public void ARuleWithAMalformedConditionIsSkipped()
    {
        var feature = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule>
            {
                new() { Condition = MalformedCondition, Force = "malformed" },
                new() { Force = "next" }
            }
        };

        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.Parse(@"{ ""id"": ""123"" }"),
            Features = new Dictionary<string, Feature> { [FeatureName] = feature }
        });

        var result = growthBook.EvalFeature(FeatureName);

        result.Value.ToString().Should().Be("next");
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void AnExperimentWithAMalformedConditionIsNotRun()
    {
        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.Parse(@"{ ""id"": ""123"" }")
        });

        var result = growthBook.Run(new Experiment
        {
            Key = "malformed-experiment",
            Variations = new JArray { 0, 1 },
            Condition = MalformedCondition
        });

        result.InExperiment.Should().BeFalse();
    }
}
