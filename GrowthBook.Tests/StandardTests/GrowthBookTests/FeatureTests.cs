using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
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
        /// <summary>
        /// The expected result as raw JSON. <see cref="ExpectedResult"/> reads it, since the JS SDK can report
        /// a <c>hashValue</c> that isn't a string.
        /// </summary>
        [TestPropertyIndex(3)]
        public JObject RawExpectedResult { get; set; }

        public FeatureResult ExpectedResult => RawExpectedResult?.ToObject<FeatureResult>(ExpectedResultSerializer);
        /// <summary>
        /// The context as raw JSON, to see payload fields that <see cref="Context"/> doesn't model.
        /// </summary>
        [TestPropertyIndex(1)]
        public JObject RawContext { get; set; }
    }

    /// <summary>
    /// Reads any non-string JSON value into a string member as its JSON text, so a numeric, array or object
    /// <c>hashValue</c> in an expected result can't break deserializing the case.
    /// </summary>
    private sealed class JsonTextStringConverter : JsonConverter<string>
    {
        public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);

            return token.Type switch
            {
                JTokenType.Null => null,
                JTokenType.String => (string)token,
                JTokenType.Array or JTokenType.Object => token.ToString(Formatting.None),
                JTokenType.Integer or JTokenType.Float => global::GrowthBook.Providers.ConditionEvaluationProvider.JavaScriptNumberText(token.Value<double>()),
                _ => Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)
            };
        }

        public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer) => writer.WriteValue(value);
    }

    private static readonly JsonSerializer ExpectedResultSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        Converters = { new JsonTextStringConverter() }
    });

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

        actualResult.Should().BeEquivalentTo(testCase.ExpectedResult, options => options.ComparingByMembers<FeatureResult>().Excluding(x => x.Value), "because every expected property value should have a matching actual property value");
        JToken.DeepEquals(testCase.ExpectedResult.Value ?? JValue.CreateNull(), actualResult.Value ?? JValue.CreateNull()).Should().BeTrue($"because the feature value should match (expected {testCase.ExpectedResult.Value?.ToString(Newtonsoft.Json.Formatting.None)}, got {actualResult.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"})");
    }
}
