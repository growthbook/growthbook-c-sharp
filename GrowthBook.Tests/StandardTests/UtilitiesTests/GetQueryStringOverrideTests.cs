using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Utilities;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class GetQueryStringOverrideTests : UnitTest
{
    [StandardCaseTestCategory("getQueryStringOverride")]
    public class GetQueryStringOverrideTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public string ExperimentKey { get; set; }
        [TestPropertyIndex(2)]
        public string Url { get; set; }
        [TestPropertyIndex(3)]
        public int NumberOfVariations { get; set; }
        [TestPropertyIndex(4)]
        public int? ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(GetQueryStringOverrideTestCase))]
    public void GetQueryStringOverride(GetQueryStringOverrideTestCase testCase)
    {
        var actualResult = ExperimentUtilities.GetQueryStringOverride(testCase.ExperimentKey, testCase.Url, testCase.NumberOfVariations);

        actualResult.Should().Be(testCase.ExpectedResult, "because the override should function correctly");
    }
}
