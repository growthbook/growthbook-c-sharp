using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class ChooseVariationTests : UnitTest
{
    [StandardCaseTestCategory("chooseVariation")]
    public class ChooseVariationTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public float HashValue { get; set; }
        [TestPropertyIndex(2)]
        public BucketRange[] BucketRanges { get; set; }
        [TestPropertyIndex(3)]
        public int ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(ChooseVariationTestCase))]
    public void ChooseVariation(ChooseVariationTestCase testCase)
    {
        var actualResult = Utilities.ChooseVariation(testCase.HashValue, testCase.BucketRanges);

        actualResult.Should().Be(testCase.ExpectedResult, "because the variation choice should be deterministic");
    }
}
