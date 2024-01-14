using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
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
        public float[] ExpectedResults { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(GetEqualWeightsTestCase))]
    public void GetEqualWeights(GetEqualWeightsTestCase testCase)
    {
        var actualResult = Utilities.GetEqualWeights(testCase.NumberOfVariations);

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResults, "because the weights should be created with an equal distribution");
    }
}
