using FluentAssertions;
using GrowthBook.Providers;
using GrowthBook.Tests.StandardTests.GrowthBookTests;
using GrowthBook.Tests.StandardTests.ProviderTests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrowthBook.Tests.StandardTests;

/// <summary>
/// Runs the standard cases in the <c>savedGroupReferencesV2</c> category, which nests its own
/// evalCondition, feature and run sections.
/// </summary>
public class SavedGroupReferencesV2Tests : UnitTest
{
    [StandardCaseTestCategory("savedGroupReferencesV2.evalCondition")]
    public class SavedGroupEvalConditionTestCase : EvalConditionTests.EvalConditionTestCase { }

    [StandardCaseTestCategory("savedGroupReferencesV2.feature")]
    public class SavedGroupFeatureTestCase : FeatureTests.FeatureTestCase { }

    [StandardCaseTestCategory("savedGroupReferencesV2.run")]
    public class SavedGroupRunTestCase : RunTests.RunTestCase { }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(SavedGroupEvalConditionTestCase))]
    public void EvalCondition(SavedGroupEvalConditionTestCase testCase)
    {
        var logger = new NullLogger<ConditionEvaluationProvider>();
        var actualResult = new ConditionEvaluationProvider(logger).EvalCondition(testCase.Attributes, testCase.Condition, testCase.Groups);

        actualResult.Should().Be(testCase.ExpectedValue, "because the condition should evaluate correctly");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(SavedGroupFeatureTestCase))]
    public void EvalFeature(SavedGroupFeatureTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.EvalFeature(testCase.FeatureName);

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResult, "because every expected property value should have a matching actual property value");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(SavedGroupRunTestCase))]
    public void Run(SavedGroupRunTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.Run(testCase.Experiment);

        actualResult.InExperiment.Should().Be(testCase.InExperiment, "because the logic placing an experiment into buckets should be correct");
        actualResult.HashUsed.Should().Be(testCase.HashUsed, "because the logic determining which hash to use should be correct");
        actualResult.Value.Should().BeEquivalentTo(testCase.ExpectedValue, "because the end result value should reflect the correct experiment decisioning");
    }
}
