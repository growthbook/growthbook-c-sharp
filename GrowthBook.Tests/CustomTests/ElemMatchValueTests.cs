using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <c>$elemMatch</c> with a value that isn't an object. The JS SDK reads the value with <c>Object.keys</c>, so a
/// number or boolean compares each element to it, while a non-empty string or an array is a condition keyed by
/// index. It never throws for a missing or non-array attribute. Expected values come from the JS SDK.
/// </summary>
public class ElemMatchValueTests : UnitTest
{
    private static readonly ConditionEvaluationProvider Evaluator = new(NullLogger<ConditionEvaluationProvider>.Instance);

    [Theory]
    // A missing attribute fails $elemMatch without throwing, so a later $or branch decides
    [InlineData(@"{ ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }", @"{ ""id"": ""u"" }", true)]
    [InlineData(@"{ ""$and"": [ { ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }, { ""country"": ""US"" } ] }", @"{ ""id"": ""u"", ""country"": ""US"" }", true)]
    [InlineData(@"{ ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }", @"{ ""id"": ""x"", ""items"": [ { ""n"": 1 }, 5 ] }", true)]
    [InlineData(@"{ ""items"": { ""$elemMatch"": 5 } }", @"{ ""items"": [ 1, 5 ] }", true)]
    [InlineData(@"{ ""items"": { ""$elemMatch"": 5 } }", @"{ ""items"": [ 1, 6 ] }", false)]
    [InlineData(@"{ ""items"": { ""$elemMatch"": 5 } }", @"{ ""items"": [ ""5"" ] }", false)]
    [InlineData(@"{ ""items"": { ""$elemMatch"": true } }", @"{ ""items"": [ false, true ] }", true)]
    [InlineData(@"{ ""items"": { ""$elemMatch"": null } }", @"{ ""id"": ""u"" }", false)]
    // A non-empty string is a condition on the element's "0", "1", ... keys
    [InlineData(@"{ ""tags"": { ""$elemMatch"": ""b"" } }", @"{ ""tags"": [ ""a"", ""b"" ] }", false)]
    [InlineData(@"{ ""tags"": { ""$elemMatch"": ""b"" } }", @"{ ""tags"": [ { ""0"": ""b"" } ] }", true)]
    [InlineData(@"{ ""tags"": { ""$elemMatch"": """" } }", @"{ ""tags"": [ ""a"", """" ] }", true)]
    public void EvaluatesLikeTheJsSdk(string condition, string attributes, bool expected)
    {
        Evaluator.EvalCondition(JObject.Parse(attributes), JObject.Parse(condition)).Should().Be(expected);
    }

    [Fact]
    public void ARuleWhoseOrPassesDespiteAnElemMatchOnAMissingAttributeIsApplied()
    {
        var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.Parse(@"{ ""id"": ""u"" }"),
            Features = new Dictionary<string, Feature>
            {
                ["f"] = new Feature
                {
                    DefaultValue = 0,
                    Rules = new List<FeatureRule>
                    {
                        new() { Condition = JObject.Parse(@"{ ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }"), Force = 1 }
                    }
                }
            }
        });

        growthBook.EvalFeature("f").Value.ToObject<int>().Should().Be(1);
    }

    [Fact]
    public void ReducePayloadAgreesWithEvaluation()
    {
        var payload = JObject.Parse(@"{ ""features"": { ""f"": { ""defaultValue"": 0, ""rules"": [
            { ""id"": ""r1"", ""condition"": { ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }, ""force"": 1 },
            { ""id"": ""r2"", ""condition"": { ""$and"": [ { ""$or"": [ { ""items"": { ""$elemMatch"": 5 } }, { ""id"": ""u"" } ] }, { ""country"": ""US"" } ] }, ""force"": 2 }
        ] } } }");

        // The JS SDK's reducePayload output for the same input
        var expected = JObject.Parse(@"{ ""features"": { ""f"": { ""defaultValue"": 0, ""rules"": [
            { ""id"": ""r1"", ""force"": 1 },
            { ""id"": ""r2"", ""condition"": { ""$and"": [ { ""country"": ""US"" } ] }, ""force"": 2 }
        ] } }, ""experiments"": [], ""savedGroups"": {} }");

        var actual = PayloadReducer.ReducePayload(payload, JObject.Parse(@"{ ""id"": ""u"" }"));

        JToken.DeepEquals(actual, expected).Should().BeTrue($"because {actual} should equal {expected}");
    }
}
