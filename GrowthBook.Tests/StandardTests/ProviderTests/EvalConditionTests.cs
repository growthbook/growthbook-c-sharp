using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public JObject Condition { get; set; }
        [TestPropertyIndex(2)]
        public JObject Attributes { get; set; }
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
        var actualResult = new ConditionEvaluationProvider(logger).EvalCondition(testCase.Attributes, testCase.Condition, JObject.FromObject(testCase.Groups));

        actualResult.Should().Be(testCase.ExpectedValue, "because the condition should evaluate correctly");
    }
}
