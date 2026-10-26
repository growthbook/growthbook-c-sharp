using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests;

/// <summary>
/// Runs the standard cases in the <c>pruneCondition</c> and <c>reducePayload</c> categories.
/// </summary>
public class PayloadReductionTests : UnitTest
{
    [StandardCaseTestCategory("pruneCondition")]
    public class PruneConditionTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public JObject Condition { get; set; }
        [TestPropertyIndex(2)]
        public JObject Attributes { get; set; }
        [TestPropertyIndex(3)]
        public JToken ExpectedValue { get; set; }
        [TestPropertyIndex(4, isOptional: true)]
        public JObject Groups { get; set; } = new JObject();
    }

    [StandardCaseTestCategory("reducePayload")]
    public class ReducePayloadTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public JObject Input { get; set; }
        [TestPropertyIndex(2)]
        public JObject ExpectedPayload { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(PruneConditionTestCase))]
    public void PruneCondition(PruneConditionTestCase testCase)
    {
        var actual = PayloadReducer.PruneCondition(testCase.Attributes, testCase.Condition, testCase.Groups);

        JToken.DeepEquals(actual, testCase.ExpectedValue).Should().BeTrue($"because {actual} should equal {testCase.ExpectedValue}");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(ReducePayloadTestCase))]
    public void ReducePayload(ReducePayloadTestCase testCase)
    {
        var input = testCase.Input;

        // The user's attributes take precedence over the global ones
        var attributes = (input["globalAttributes"] as JObject)?.DeepClone() as JObject ?? new JObject();
        attributes.Merge(input["attributes"] as JObject ?? new JObject());

        var options = new ReducePayloadOptions
        {
            Include = ReadFilters(input["include"]),
            Exclude = ReadFilters(input["exclude"]),
            Prerequisites = input["prerequisites"]?.ToString() == "filter" ? PrerequisiteFiltering.Filter : PrerequisiteFiltering.Keep
        };

        var actual = PayloadReducer.ReducePayload((JObject)input["payload"], attributes, options);

        JToken.DeepEquals(actual, testCase.ExpectedPayload).Should().BeTrue($"because {actual} should equal {testCase.ExpectedPayload}");
    }

    [Fact]
    public void ReducePayloadDoesNotModifyItsInput()
    {
        var payload = JObject.Parse(@"{ ""features"": { ""f"": { ""defaultValue"": 0, ""rules"": [ { ""condition"": { ""id"": ""123"" }, ""force"": 1 } ] } } }");
        var original = payload.DeepClone();

        PayloadReducer.ReducePayload(payload, JObject.Parse(@"{ ""id"": ""123"" }"));

        JToken.DeepEquals(payload, original).Should().BeTrue();
    }

    private static PayloadFilters ReadFilters(JToken filters)
    {
        if (!(filters is JObject obj))
        {
            return null;
        }

        return new PayloadFilters
        {
            Projects = (obj["projects"] as JArray)?.Select(x => x.ToString()).ToList(),
            Tags = (obj["tags"] as JArray)?.Select(x => x.ToString()).ToList(),
            CustomFields = (obj["customFields"] as JObject)?.Properties()
                .ToDictionary(x => x.Name, x => (IList<JToken>)((x.Value as JArray)?.ToList() ?? new List<JToken>()))
        };
    }
}
