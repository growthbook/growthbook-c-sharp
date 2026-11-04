using System.Collections.Generic;
using FluentAssertions;
using GrowthBook;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests
{
    public class RemoteEvaluationRequestTests
    {
        [Fact]
        public void FromContext_ShouldWorkCorrectly()
        {
            // Test with valid context
            var context = new Context
            {
                Attributes = JObject.Parse(@"{""userId"": ""123""}"),
                ForcedVariations = new Dictionary<string, int> { { "exp1", 1 } },
                Url = "https://example.com"
            };

            var request = RemoteEvaluationRequest.FromContext(context);
            request.Should().NotBeNull();
            request.Attributes["userId"].ToString().Should().Be("123");
            request.ForcedVariations["exp1"].Should().Be(1);
            request.Url.Should().Be("https://example.com");

            // Test with null context
            var request2 = RemoteEvaluationRequest.FromContext(null);
            request2.Should().NotBeNull();
            request2.Attributes.Should().NotBeNull();
            request2.ForcedVariations.Should().NotBeNull();
        }

        [Fact]
        public void JsonSerialization_ShouldUseCamelCase()
        {
            var request = new RemoteEvaluationRequest
            {
                Attributes = JObject.Parse(@"{""userId"": ""123""}"),
                ForcedVariations = new Dictionary<string, int> { { "exp1", 1 } }
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            json.Should().Contain("\"attributes\":");
            json.Should().Contain("\"forcedVariations\":");
        }

        [Fact]
        public void FromContext_ShouldCarryTheForcedFeatureValues()
        {
            var context = new Context
            {
                ForcedFeatures = new Dictionary<string, object>
                {
                    { "dark-mode", true },
                    { "banner-text", "hello" }
                }
            };

            var request = RemoteEvaluationRequest.FromContext(context);

            request.ForcedFeatures.Should().BeEquivalentTo(new[]
            {
                new List<object> { "dark-mode", true },
                new List<object> { "banner-text", "hello" }
            });
        }

        [Fact]
        public void JsonSerialization_ShouldSendForcedFeaturesAsAnArrayOfPairs()
        {
            var request = new RemoteEvaluationRequest
            {
                ForcedFeatures = new List<List<object>> { new List<object> { "dark-mode", true } }
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);

            // Sending an object rather than an array of pairs makes the API reject the request with 400
            json.Should().Contain("\"forcedFeatures\":[[\"dark-mode\",true]]");
        }

        /// <summary>
        /// The value a feature is forced to is whatever the feature returns, so every JSON type has to
        /// survive the trip through <see cref="RemoteEvaluationRequest.FromContext"/> and out again as the
        /// second item of its pair. Null is the one worth stating: forcing a feature to null is a real
        /// override rather than an absent one, and dropping it would silently change what the server
        /// evaluates.
        /// </summary>
        [Theory]
        [InlineData(true, "true")]
        [InlineData(false, "false")]
        [InlineData(null, "null")]
        [InlineData(42, "42")]
        [InlineData(3.5, "3.5")]
        [InlineData("hello", "\"hello\"")]
        public void JsonSerialization_ShouldKeepEachForcedFeatureValueAsTheSecondItemOfItsPair(object value, string expectedJson)
        {
            var context = new Context
            {
                ForcedFeatures = new Dictionary<string, object> { { "dark-mode", value } }
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(RemoteEvaluationRequest.FromContext(context));

            json.Should().Contain($"\"forcedFeatures\":[[\"dark-mode\",{expectedJson}]]");
        }

        [Fact]
        public void JsonSerialization_ShouldKeepStructuredForcedFeatureValuesIntact()
        {
            var context = new Context
            {
                ForcedFeatures = new Dictionary<string, object>
                {
                    { "limits", new Dictionary<string, object> { { "max", 5 }, { "enabled", true } } },
                    { "tiers", new[] { 1, 2, 3 } },
                    { "payload", JObject.Parse(@"{""nested"":{""deep"":true}}") }
                }
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(RemoteEvaluationRequest.FromContext(context));

            // Asserted pair by pair rather than as a whole array, so the test does not depend on the order
            // the dictionary happens to enumerate in.
            json.Should().Contain("[\"limits\",{\"max\":5,\"enabled\":true}]");
            json.Should().Contain("[\"tiers\",[1,2,3]]");
            json.Should().Contain("[\"payload\",{\"nested\":{\"deep\":true}}]");
        }

        [Fact]
        public void FromContext_ShouldKeepAFeatureForcedToNullRatherThanDroppingIt()
        {
            var context = new Context
            {
                ForcedFeatures = new Dictionary<string, object>
                {
                    { "dark-mode", null },
                    { "banner-text", "hello" }
                }
            };

            var request = RemoteEvaluationRequest.FromContext(context);

            request.ForcedFeatures.Should().HaveCount(2, "because forcing a feature to null is an override, not the absence of one");
            request.ForcedFeatures.Should().ContainEquivalentOf(new List<object> { "dark-mode", null });
        }
    }
}
