using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

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
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(EvalConditionTestCase))]
    public void EvalCondition(EvalConditionTestCase testCase)
    {
        var actualResult = Utilities.EvalCondition(testCase.Attributes, testCase.Condition);

        actualResult.Should().Be(testCase.ExpectedValue, "because the condition should evaluate correctly");
    }
}
