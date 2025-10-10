using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.ProviderTests;

public class EvalConditionTests : UnitTest
{
    [StandardCaseTestCategory("evalCondition")]
    public class EvalConditionTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public JsonObject Condition { get; set; }
        [TestPropertyIndex(2)]
        public JsonObject Attributes { get; set; }
        [TestPropertyIndex(3)]
        public bool ExpectedValue { get; set; }
        [TestPropertyIndex(4, isOptional: true)]
        public Dictionary<string, object[]> Groups { get; set; } = new Dictionary<string, object[]>();
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(EvalConditionTestCase))]
    public void EvalCondition(EvalConditionTestCase testCase)
    {
        var logger = new NullLogger<ConditionEvaluationProvider>();
        var groupsJsonObject = JsonSerializer.SerializeToNode(testCase.Groups).AsObject();
        if (groupsJsonObject == null)
        {
            throw new InvalidOperationException("Failed to convert Groups dictionary to JsonObject.");
        }
        var actualResult = new ConditionEvaluationProvider(logger).EvalCondition(testCase.Attributes, testCase.Condition, groupsJsonObject);

        actualResult.Should().Be(testCase.ExpectedValue, "because the condition should evaluate correctly");
    }
}
