using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Behaviour that must match the JS SDK (spec 0.12.0) and that the standard cases don't cover.
/// </summary>
public class JsParityTests
{
    private static bool Eval(string attributes, string condition, string savedGroups = "{}") =>
        new ConditionEvaluationProvider(new NullLogger<ConditionEvaluationProvider>())
            .EvalCondition(JObject.Parse(attributes), JObject.Parse(condition), JObject.Parse(savedGroups));

    [Theory]
    [InlineData(@"{ ""a"": [] }", @"{ ""a"": { ""$eq"": [] } }")]
    [InlineData(@"{ ""a"": [1] }", @"{ ""a"": { ""$eq"": [1] } }")]
    [InlineData(@"{ ""a"": {} }", @"{ ""a"": { ""$eq"": {} } }")]
    [InlineData(@"{ ""a"": { ""b"": 1 } }", @"{ ""a"": { ""$eq"": { ""b"": 1 } } }")]
    public void EqNeverMatchesAnArrayOrObject(string attributes, string condition)
    {
        // JS compares with ===, so an array or object is only equal to itself
        Eval(attributes, condition).Should().BeFalse();
        Eval(attributes, condition.Replace("$eq", "$ne")).Should().BeTrue();
    }

    [Fact]
    public void EqStillComparesScalarsAndNumbersByValue()
    {
        Eval(@"{ ""a"": 1 }", @"{ ""a"": { ""$eq"": 1.0 } }").Should().BeTrue();
        Eval(@"{ ""a"": ""x"" }", @"{ ""a"": { ""$ne"": ""x"" } }").Should().BeFalse();
    }

    [Theory]
    [InlineData("$regex", "1", @"""10""")]
    [InlineData("$regex", "true", @"""true""")]
    [InlineData("$regexi", "1", @"""1.0""")]
    [InlineData("$regex", "null", @"""null""")]
    public void ARegexWithANonStringPatternFails(string op, string pattern, string value)
    {
        Eval($@"{{ ""a"": {value} }}", $@"{{ ""a"": {{ ""{op}"": {pattern} }} }}").Should().BeFalse();
    }

    [Fact]
    public void NumbersInListsCompareByValue()
    {
        Eval(@"{ ""a"": 1.0 }", @"{ ""a"": { ""$in"": [1, 2] } }").Should().BeTrue();
        Eval(@"{ ""a"": [1] }", @"{ ""a"": { ""$in"": [1.0] } }").Should().BeTrue();
        Eval(@"{ ""a"": 1 }", @"{ ""a"": { ""$nin"": [1.0] } }").Should().BeFalse();
        Eval(@"{ ""a"": 2 }", @"{ ""a"": { ""$inGroup"": ""g"" } }", @"{ ""g"": [1.0, 2.0] }").Should().BeTrue();
    }

    [Fact]
    public void AnArrayOrObjectInAListIsNeverAMember()
    {
        Eval(@"{ ""a"": [1] }", @"{ ""a"": { ""$in"": [[1]] } }").Should().BeFalse();
        Eval(@"{ ""a"": { ""b"": 1 } }", @"{ ""a"": { ""$nin"": [{ ""b"": 1 }] } }").Should().BeTrue();
    }

    [Theory]
    [InlineData(@"{ ""tags"": [""a"", ""b""] }", @"{ ""tags.1"": ""b"" }", true)]
    [InlineData(@"{ ""a"": [{ ""b"": 2 }] }", @"{ ""a.0.b"": 2 }", true)]
    [InlineData(@"{ ""tags"": [""a""] }", @"{ ""tags.1"": { ""$exists"": false } }", true)]
    [InlineData(@"{ ""tags"": [""a""] }", @"{ ""tags.01"": { ""$exists"": true } }", false)]
    [InlineData(@"{ ""a"": { ""b"": 1 } }", @"{ ""a.b"": 1 }", true)]
    public void APathSegmentIndexesIntoAnArray(string attributes, string condition, bool expected)
    {
        Eval(attributes, condition).Should().Be(expected);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData(@"""""")]
    [InlineData("[]")]
    [InlineData(@"[""1""]")]
    [InlineData("{}")]
    [InlineData("null")]
    public void AnIdThatIsntANonEmptyStringOrNonZeroNumberIsMissing(string id)
    {
        var result = new GrowthBook(new Context { Attributes = JObject.Parse($@"{{ ""id"": {id} }}") })
            .Run(new Experiment { Key = "exp", Variations = new JArray { 0, 1 } });

        result.InExperiment.Should().BeFalse();
        result.HashUsed.Should().BeFalse();
        result.HashValue.Should().BeEmpty();
    }

    [Fact]
    public void AWhitespaceIdIsHashed()
    {
        var result = new GrowthBook(new Context { Attributes = JObject.Parse(@"{ ""id"": "" "" }") })
            .Run(new Experiment { Key = "exp", Variations = new JArray { 0, 1 } });

        result.InExperiment.Should().BeTrue();
        result.HashValue.Should().Be(" ");
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1.5", "1.5")]
    [InlineData("-3", "-3")]
    public void ANumericIdHashesLikeJavaScriptsString(string id, string text)
    {
        var experiment = new Experiment { Key = "exp", Variations = new JArray { 0, 1 }, HashVersion = 2 };

        var numeric = new GrowthBook(new Context { Attributes = JObject.Parse($@"{{ ""id"": {id} }}") }).Run(experiment);
        var textual = new GrowthBook(new Context { Attributes = new JObject { ["id"] = text } }).Run(experiment);

        numeric.HashValue.Should().Be(text);
        numeric.Bucket.Should().Be(textual.Bucket);
        numeric.VariationId.Should().Be(textual.VariationId);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnUnusableIdFallsBackToTheFallbackAttributeWithStickyBucketing(string id)
    {
        var context = new Context
        {
            Attributes = JObject.Parse($@"{{ ""id"": {id}, ""deviceId"": ""d1"" }}"),
            StickyBucketService = new Services.InMemoryStickyBucketService()
        };

        var result = new GrowthBook(context).Run(new Experiment { Key = "exp", Variations = new JArray { 0, 1 }, FallbackAttribute = "deviceId" });

        result.InExperiment.Should().BeTrue();
        result.HashAttribute.Should().Be("deviceId");
        result.HashValue.Should().Be("d1");
    }

    [Fact]
    public void TheFallbackAttributeIsIgnoredWithoutStickyBucketing()
    {
        var context = new Context { Attributes = JObject.Parse(@"{ ""id"": true, ""deviceId"": ""d1"" }") };

        var result = new GrowthBook(context).Run(new Experiment { Key = "exp", Variations = new JArray { 0, 1 }, FallbackAttribute = "deviceId" });

        result.InExperiment.Should().BeFalse();
        result.HashAttribute.Should().Be("id");
    }

    [Fact]
    public void ARolloutUsesTheFallbackAttributeWithStickyBucketing()
    {
        var feature = new Feature
        {
            DefaultValue = "off",
            Rules = new List<FeatureRule> { new() { Force = "on", Coverage = 1, HashAttribute = "company", FallbackAttribute = "deviceId" } }
        };

        Context NewContext(bool sticky) => new()
        {
            Attributes = JObject.Parse(@"{ ""deviceId"": ""d1"" }"),
            Features = new Dictionary<string, Feature> { ["f"] = feature },
            StickyBucketService = sticky ? new Services.InMemoryStickyBucketService() : null
        };

        new GrowthBook(NewContext(sticky: true)).EvalFeature("f").Value.ToString().Should().Be("on");
        new GrowthBook(NewContext(sticky: false)).EvalFeature("f").Value.ToString().Should().Be("off");
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData(@"""""", false)]
    [InlineData(@"""off""", true)]
    [InlineData(@"""false""", true)]
    [InlineData(@"""0""", true)]
    [InlineData("[]", true)]
    [InlineData("{}", true)]
    [InlineData("0.5", true)]
    public void OnFollowsJavaScriptTruthiness(string value, bool on)
    {
        var result = new FeatureResult { Value = JToken.Parse(value) };

        result.On.Should().Be(on);
        result.Off.Should().Be(!on);
    }
}
