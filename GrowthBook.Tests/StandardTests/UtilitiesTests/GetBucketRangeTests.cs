using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Tests.Extensions;
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
        public float? Coverage { get; set; }
        [TestPropertyIndex(2)]
        public float[] Weights { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(BucketRangeTestCase))]
    public void GetBucketRanges(BucketRangeTestCase testCase)
    {
        var config = testCase.Configuration;

        var actualResults = Utilities.GetBucketRanges(config.NumberOfVariations, config.Coverage ?? 1f, config.Weights);
        var roundedResults = actualResults.Select(x => new BucketRange(x.Start.Round(), x.End.Round()));
        roundedResults.Should().BeEquivalentTo(testCase.ExpectedResults, "because the bucketing logic should be deterministic");
    }
}
