using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Runs the condition cases this fork added on top of the shared spec suite.
/// </summary>
/// <remarks>
/// They used to live in standard-cases.json, where a spec bump would silently delete them. Keeping them in
/// custom-cases.json means the vendored suite can be replaced wholesale without losing local coverage.
/// </remarks>
public class CustomEvalConditionTests : UnitTest
{
    [CustomCaseTestCategory("evalCondition")]
    public class CustomEvalConditionTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public JObject Condition { get; set; }
        [TestPropertyIndex(2)]
        public JObject Attributes { get; set; }
        [TestPropertyIndex(3)]
        public bool ExpectedValue { get; set; }
        [TestPropertyIndex(4, isOptional: true)]
        public Dictionary<string, object[]> Groups { get; set; } = new Dictionary<string, object[]>();
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(CustomEvalConditionTestCase))]
    public void EvalCondition(CustomEvalConditionTestCase testCase)
    {
        var logger = new NullLogger<ConditionEvaluationProvider>();
        var actualResult = new ConditionEvaluationProvider(logger).EvalCondition(testCase.Attributes, testCase.Condition, JObject.FromObject(testCase.Groups));

        actualResult.Should().Be(testCase.ExpectedValue, "because the condition should evaluate correctly");
    }
}
