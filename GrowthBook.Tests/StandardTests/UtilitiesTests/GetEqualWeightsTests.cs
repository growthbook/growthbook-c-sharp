using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Tests.Extensions;
using GrowthBook.Utilities;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class GetEqualWeightsTests : UnitTest
{
    [StandardCaseTestCategory("getEqualWeights")]
    public class GetEqualWeightsTestCase
    {
        [TestPropertyIndex(0)]
        public int NumberOfVariations { get; set; }
        [TestPropertyIndex(1)]
        public double[] ExpectedResults { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(GetEqualWeightsTestCase))]
    public void GetEqualWeights(GetEqualWeightsTestCase testCase)
    {
        // Both sides are floored to 8 places, as in the JS SDK's spec runner
        var actualResult = ExperimentUtilities.GetEqualWeights(testCase.NumberOfVariations);

        actualResult.Select(x => x.Floor8()).Should().Equal(testCase.ExpectedResults.Select(x => x.Floor8()), "because the weights should be created with an equal distribution");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void GetEqualWeightsMatchesDoublePrecisionDivision(int numberOfVariations)
    {
        var actualResult = ExperimentUtilities.GetEqualWeights(numberOfVariations);

        actualResult.Should().AllSatisfy(x => x.Should().Be(1.0 / numberOfVariations), "because JS computes equal weights as 1/n in double precision");
    }
}
