using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class UrlRedirectTests : UnitTest
{
    [StandardCaseTestCategory("urlRedirect")]
    public class UrlRedirectTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public JToken[] ExpectedResults { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(UrlRedirectTestCase))]
    public void Run(UrlRedirectTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
    }
}
