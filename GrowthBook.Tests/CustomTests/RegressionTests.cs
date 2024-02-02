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
