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

public class GetBucketRangeTests : UnitTest
{
    [StandardCaseTestCategory("getBucketRange")]
    public class BucketRangeTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public BucketRangeConfiguration Configuration { get; set; }
        [TestPropertyIndex(2)]
        public BucketRange[] ExpectedResults { get; set; }
    }

    public class BucketRangeConfiguration
    {
        [TestPropertyIndex(0)]
        public int NumberOfVariations { get; set; }
        [TestPropertyIndex(1)]
        public double? Coverage { get; set; }
        [TestPropertyIndex(2)]
        public double[] Weights { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(BucketRangeTestCase))]
    public void GetBucketRanges(BucketRangeTestCase testCase)
    {
        var config = testCase.Configuration;

        // Both sides are floored to 8 places, as in the JS SDK's spec runner
        var actualResults = ExperimentUtilities.GetBucketRanges(config.NumberOfVariations, config.Coverage ?? 1.0, config.Weights)
            .Select(x => new BucketRange(x.Start.Floor8(), x.End.Floor8()));
        var expectedResults = testCase.ExpectedResults.Select(x => new BucketRange(x.Start.Floor8(), x.End.Floor8()));

        actualResults.Should().Equal(expectedResults, "because the bucketing logic should be deterministic");
    }

    // Exact range boundaries produced by the JS SDK (start + coverage * weight in double precision).
    public static TheoryData<int, double, double[], BucketRange[]> JsReferenceRanges => new()
    {
        { 5, 0.1, new[] { 0.2, 0.2, 0.2, 0.2, 0.2 }, new[] { new BucketRange(0.0, 0.020000000000000004), new BucketRange(0.2, 0.22000000000000003), new BucketRange(0.4, 0.42000000000000004), new BucketRange(0.6000000000000001, 0.6200000000000001), new BucketRange(0.8, 0.8200000000000001) } },
        { 5, 0.72, new[] { 0.2, 0.2, 0.2, 0.2, 0.2 }, new[] { new BucketRange(0.0, 0.144), new BucketRange(0.2, 0.344), new BucketRange(0.4, 0.544), new BucketRange(0.6000000000000001, 0.7440000000000001), new BucketRange(0.8, 0.9440000000000001) } },
        { 2, 0.01, new[] { 0.1, 0.9 }, new[] { new BucketRange(0.0, 0.001), new BucketRange(0.1, 0.10900000000000001) } },
        { 2, 0.74, new[] { 0.3, 0.7 }, new[] { new BucketRange(0.0, 0.222), new BucketRange(0.3, 0.8180000000000001) } },
        { 4, 0.36, new[] { 0.1, 0.2, 0.3, 0.4 }, new[] { new BucketRange(0.0, 0.036), new BucketRange(0.1, 0.172), new BucketRange(0.30000000000000004, 0.40800000000000003), new BucketRange(0.6000000000000001, 0.7440000000000001) } },
        { 3, 0.29, new[] { 0.2, 0.3, 0.5 }, new[] { new BucketRange(0.0, 0.057999999999999996), new BucketRange(0.2, 0.28700000000000003), new BucketRange(0.5, 0.645) } },
        { 3, 0.48, new[] { 0.2, 0.3, 0.5 }, new[] { new BucketRange(0.0, 0.096), new BucketRange(0.2, 0.344), new BucketRange(0.5, 0.74) } },
    };

    [Theory]
    [MemberData(nameof(JsReferenceRanges))]
    public void GetBucketRangesMatchesJsExactly(int numberOfVariations, double coverage, double[] weights, BucketRange[] expected)
    {
        var actualResults = ExperimentUtilities.GetBucketRanges(numberOfVariations, coverage, weights);

        actualResults.Should().Equal(expected, "because range boundaries must match the JS SDK bit for bit");
        ExperimentUtilities.GetBucketRanges(numberOfVariations, coverage).Should().Equal(
            ExperimentUtilities.GetBucketRanges(numberOfVariations, coverage, ExperimentUtilities.GetEqualWeights(numberOfVariations)),
            "because omitted weights should behave like explicit equal weights");
    }
}
