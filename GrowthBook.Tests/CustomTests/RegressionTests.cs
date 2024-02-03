using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class RegressionTests : UnitTest
{
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
