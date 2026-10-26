using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Inputs where <see cref="PayloadReducer"/> must agree with the JavaScript SDK's <c>pruneCondition</c> and
/// <c>reducePayload</c>, with expected values taken from the JavaScript SDK.
/// </summary>
public class PayloadReducerParityTests
{
    private static JObject Features(JObject result) => (JObject)result["features"];

    private static JObject CustomFieldPayload() => JObject.Parse(@"{ ""features"": {
        ""explicitNull"": { ""defaultValue"": true, ""metadata"": { ""customFields"": { ""region"": null } } },
        ""missing"": { ""defaultValue"": true, ""metadata"": { ""customFields"": { } } },
        ""set"": { ""defaultValue"": true, ""metadata"": { ""customFields"": { ""region"": ""eu"" } } }
    } }");

    [Fact]
    public void AnExplicitNullCustomFieldMatchesANullFilter()
    {
        var nullFilter = new Dictionary<string, IList<JToken>> { ["region"] = new List<JToken> { JValue.CreateNull() } };

        var excluded = PayloadReducer.ReducePayload(CustomFieldPayload(), options: new ReducePayloadOptions { Exclude = new PayloadFilters { CustomFields = nullFilter } });
        var included = PayloadReducer.ReducePayload(CustomFieldPayload(), options: new ReducePayloadOptions { Include = new PayloadFilters { CustomFields = nullFilter } });

        // A missing field is undefined in JavaScript, which isn't null
        Features(excluded).Properties().Should().NotContain(x => x.Name == "explicitNull");
        Features(excluded).Should().ContainKeys("missing", "set");
        Features(included).Should().ContainKey("explicitNull");
        Features(included).Should().NotContainKeys("missing", "set");
    }
}
