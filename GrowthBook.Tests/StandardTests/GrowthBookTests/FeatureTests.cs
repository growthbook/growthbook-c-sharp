using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class FeatureTests : UnitTest
{
    [StandardCaseTestCategory("feature")]
    public class FeatureTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public string FeatureName { get; set; }
        [TestPropertyIndex(3)]
        public FeatureResult ExpectedResult { get; set; }
        /// <summary>
        /// The context as raw JSON, to see payload fields that <see cref="Context"/> doesn't model.
        /// </summary>
        [TestPropertyIndex(1)]
        public JObject RawContext { get; set; }
    }

    /// <summary>
    /// Gets the feature cases, leaving out those that need contextual bandits, which this SDK doesn't implement yet.
    /// </summary>
    public static IEnumerable<object[]> GetSupportedFeatureTests() =>
        GetMappedTestsInCategory(typeof(FeatureTestCase))
            .Where(data => ((FeatureTestCase)data[0]).RawContext?["contextualBandits"] is null);

    [Theory]
    [MemberData(nameof(GetSupportedFeatureTests))]
    public void EvalFeature(FeatureTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.EvalFeature(testCase.FeatureName);

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResult, "because every expected property value should have a matching actual property value");
    }
}
