using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class FeatureTests : UnitTest
{
    [StandardCaseTestCategory("feature")]
    public class FeatureTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public string FeatureName { get; set; }
        [TestPropertyIndex(3)]
        public FeatureResult ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(FeatureTestCase))]
    public void EvalFeature(FeatureTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.EvalFeature(testCase.FeatureName);

        actualResult.Should().BeEquivalentTo(
            testCase.ExpectedResult,
            "because every expected property value should have a matching actual property value " + testCase.TestName);
    }
}
