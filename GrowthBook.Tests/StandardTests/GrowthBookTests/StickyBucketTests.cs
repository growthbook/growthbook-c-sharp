using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class StickyBucketTests : UnitTest
{
    [StandardCaseTestCategory("stickyBucket")]
    public class StickyBucketTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public StickyAssignmentsDocument[] PreExistingAssignmentDocs { get; set; } = [];
        [TestPropertyIndex(3)]
        public string FeatureName { get; set; }
        [TestPropertyIndex(4)]
        public JToken ExpectedResult { get; set; }
        [TestPropertyIndex(5)]
        public StickyAssignmentsDocument[] ExpectedAssignmentDocs { get; set; } = [];
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(StickyBucketTestCase))]
    public void Run(StickyBucketTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
#warning Complete this.
    }
}
