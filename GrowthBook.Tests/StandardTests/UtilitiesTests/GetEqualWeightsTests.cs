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
        // SPEC DIFFERENCE: The 0.5.2 spec version calls out that these tests are expecting weights rounded to 8 decimal places,
        //                  but in .Net that will cause a rounding difference of 0.00000001 for one of these tests
        //                  and cause the test to fail. Comparing floating point values for direct equality is difficult
        //                  and not generally a recommended activity in any case and so we're backing down to 7 points of
        //                  precision to get as close of an approximation that works for .Net specifically.

        var actualResult = ExperimentUtilities.GetEqualWeights(testCase.NumberOfVariations).RoundAll(7);

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResults.RoundAll(7), "because the weights should be created with an equal distribution");
    }
}
