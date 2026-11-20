using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class ContextualBanditTests : UnitTest
{
    [StandardCaseTestCategory("contextualBandit")]
    public class ContextualBanditTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public string FeatureName { get; set; }
        [TestPropertyIndex(3)]
        public FeatureResult ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(ContextualBanditTestCase))]
    public void EvalFeature(ContextualBanditTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.EvalFeature(testCase.FeatureName);

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResult, "because every expected property value should have a matching actual property value");
    }
}
