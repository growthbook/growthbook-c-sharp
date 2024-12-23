using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class UrlRedirectTests : UnitTest
{
    public sealed class TestResult
    {
        public bool InExperiment { get; set; }
        public string UrlRedirect { get; set; }
        public string UrlWithParams { get; set; }
    }

    [StandardCaseTestCategory("urlRedirect")]
    public class UrlRedirectTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public TestResult[] ExpectedResults { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(UrlRedirectTestCase))]
    public void Run(UrlRedirectTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);

#warning Is this only applicable for auto experiments? Need more clarity around usage/logic as well.
        
        //for(var i = 0; i < gb.Experiments.Count; i++)
        //{
        //    var experiment = gb.Experiments[i];
        //    var expectedResult = testCase.ExpectedResults[i];

        //    var result = gb.Run(experiment);

        //    result.InExperiment.Should().Be(expectedResult.InExperiment);
        //    result.Value["urlRedirect"]?.ToString().Should().Be(expectedResult.UrlRedirect);
        //}
    }
}
