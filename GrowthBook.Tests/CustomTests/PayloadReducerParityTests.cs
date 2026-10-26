using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Inputs where <see cref="PayloadReducer"/> must agree with the JavaScript SDK's <c>pruneCondition</c> and
/// <c>reducePayload</c>, with expected values taken from the JavaScript SDK.
/// </summary>
public class PayloadReducerParityTests
{
    private readonly ConditionEvaluationProvider _provider = new(new NullLogger<ConditionEvaluationProvider>());

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

    [Theory]
    [InlineData(1d, "1")]
    [InlineData(1.5d, "1.5")]
    [InlineData(1e21d, "1e+21")]
    [InlineData(1e20d, "100000000000000000000")]
    [InlineData(1e-7d, "1e-7")]
    [InlineData(0.000002d, "0.000002")]
    [InlineData(123e-20d, "1.23e-18")]
    [InlineData(1e23d, "1e+23")]
    [InlineData(-0d, "0")]
    [InlineData(-1.5d, "-1.5")]
    [InlineData(0.1d, "0.1")]
    [InlineData(123456.789d, "123456.789")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void NumbersAreWrittenTheWayJavaScriptWritesThem(double value, string expected)
    {
        ConditionEvaluationProvider.JavaScriptNumberText(value).Should().Be(expected);
    }

    [Fact]
    public void ANumericVersionIsPaddedFromItsJavaScriptText()
    {
        var attributes = JObject.Parse(@"{ ""a"": 0.000002 }");
        var condition = JObject.Parse(@"{ ""a"": { ""$vgt"": 1.5 } }");

        // "0.000002" is below "1.5"; .NET's "2E-06" would sort above it
        _provider.EvalCondition(attributes, condition).Should().BeFalse();
        ((bool)PayloadReducer.PruneCondition(attributes, condition)).Should().BeFalse();
        RuleStaysConditional(attributes, condition);
    }

    // A rule whose condition fails for the user must not become unconditional
    private static void RuleStaysConditional(JObject attributes, JObject condition)
    {
        var payload = new JObject
        {
            ["features"] = new JObject
            {
                ["f"] = new JObject
                {
                    ["defaultValue"] = false,
                    ["rules"] = new JArray(new JObject { ["condition"] = condition.DeepClone(), ["force"] = true })
                }
            }
        };
        var rules = (JArray)PayloadReducer.ReducePayload(payload, attributes)["features"]["f"]["rules"];

        rules.Should().NotContain(rule => JToken.DeepEquals(rule, JObject.Parse(@"{ ""force"": true }")));
    }
}
