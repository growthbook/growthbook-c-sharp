using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class RegressionTests : UnitTest
{
    [Fact]
    public void ExperimentWillResultInAssignmentIfDifferentFromPreviousOrMissing()
    {
        const string FeatureName = "test-assignment";

        var feature = new Feature { DefaultValue = false, Rules = new List<FeatureRule> { new() { Coverage = 1d } } };

        var staticFeatures = new Dictionary<string, Feature>
        {
            [FeatureName] = feature
        };

        var context = new Context
        {
            Features = staticFeatures
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);
        var result2 = growthBook.EvalFeature(FeatureName);

        var assignmentsByExperimentKey = growthBook.GetAllResults();

        assignmentsByExperimentKey.Should().NotBeNull("because it must always be a valid collection instance");
        assignmentsByExperimentKey.Count.Should().Be(1, "because the second experiment was not different");
    }

    [Fact]
    public void GetFeatureValueShouldReturnForcedValueEvenWhenTracksIsNull()
    {
        const string FeatureName = "test-tracks-default-value";
        var forcedResult = JToken.FromObject(true);

        var feature = new Feature { DefaultValue = false, Rules = new List<FeatureRule> { new() { Force = forcedResult } } };

        var staticFeatures = new Dictionary<string, Feature>
        {
            [FeatureName] = feature
        };

        var trackingWasCalled = false;

        var context = new Context
        {
            Features = staticFeatures,
            TrackingCallback = (features, tracking) => trackingWasCalled = true
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Should().NotBeNull("because a result must be created");
        result.Value.Should().NotBeNull("because the result value was forced");
        result.Value.ToString().Should().Be(forcedResult.ToString(), "because that was the forced value");
        trackingWasCalled.Should().BeFalse("because no tracking data was present");
    }

    [Fact]
    public void GetFeatureValueShouldOnlyReturnFallbackValueWhenTheFeatureResultValueIsNull()
    {
        const string FeatureName = "test-false-default-value";

        var feature = new Feature { DefaultValue = false };

        var staticFeatures = new Dictionary<string, Feature>
        {
            [FeatureName] = feature
        };

        var context = new Context
        {
            Features = staticFeatures
        };

        var growthBook = new GrowthBook(context);

        growthBook.IsOn(FeatureName).Should().BeFalse("because the value of the feature is considered falsy");
        growthBook.GetFeatureValue(FeatureName, true).Should().BeFalse("because the default value can be served from the feature");

        growthBook.Features[FeatureName] = new Feature { DefaultValue = null };

        growthBook.IsOn(FeatureName).Should().BeFalse("because the value of the feature is considered falsy");
        growthBook.GetFeatureValue(FeatureName, false).Should().BeFalse("because the fallback value should be returned when the result value is null");

        growthBook.Features[FeatureName] = new Feature { DefaultValue = true };

        growthBook.IsOn(FeatureName).Should().BeTrue("because the value of the feature is considered truthy");
        growthBook.GetFeatureValue(FeatureName, false).Should().BeTrue("because the default value can be served from the feature");
    }

    [Fact]
    public void EvalIsInConditionShouldNotDifferWhenAttributeIsEmptyInsteadOfNull()
    {
        var featureJson = """
            {
                "defaultValue": false,
                "rules": [
                    {
                        "condition": {
                        "userId": {
                            "$in": [
                            "ac1",
                            "ac2",
                            "ac3"
                            ]
                        }
                        },
                        "force": true
                    }
                ]
            }
            """;

        var feature = JsonConvert.DeserializeObject<Feature>(featureJson);

        var staticFeatures = new Dictionary<string, Feature>
        {
            ["test-in-op"] = feature
        };

        var context = new Context
        {
            Features = staticFeatures
        };

        var growthBook = new GrowthBook(context);

        // The initial evaluation will use a null userId value.

        var flag = growthBook.IsOn("test-in-op");
        flag.Should().BeFalse("because the userId property in the attributes JSON is null");

        // Try again with a userId that is an empty string instead.

        context.Attributes.Add("userId", string.Empty);

        var errorFlag = growthBook.IsOn("test-in-op");
        errorFlag.Should().BeFalse("because an empty string is considered falsy and should not differ from the null case");
    }
}
