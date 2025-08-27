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
    }
}
