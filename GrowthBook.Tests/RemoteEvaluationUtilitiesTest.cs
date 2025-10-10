using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Utilities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.Utilities
{
    public class RemoteEvaluationUtilitiesTests
    {
        [Fact]
        public void GenerateCacheKey_WithValidContext_ShouldWork()
        {
            var context = new Context
            {
                RemoteEval = true,
                ApiHost = "https://api.example.com",
                ClientKey = "test-key",
                Attributes = JsonNode.Parse(@"{""userId"": ""123"", ""plan"": ""basic""}")!.AsObject(),
                CacheKeyAttributes = new[] { "userId" }
            };

            var result = RemoteEvaluationUtilities.GenerateCacheKey(context);

            result.Should().NotBeNull();
            result.Should().StartWith("https://api.example.com||test-key||");
            result.Should().Contain("userId");
            result.Should().NotContain("plan"); // Should be filtered out
        }

        [Fact]
        public void GenerateCacheKey_WithRemoteEvalDisabled_ShouldReturnNull()
        {
            var context = new Context { RemoteEval = false };
            var result = RemoteEvaluationUtilities.GenerateCacheKey(context);
            result.Should().BeNull();
        }

        [Fact]
        public void ShouldTriggerRemoteEvaluation_ShouldDetectChanges()
        {
            var oldAttributes = JsonNode.Parse(@"{""userId"": ""123"", ""plan"": ""basic""}")!.AsObject();
            var newAttributes = JsonNode.Parse(@"{""userId"": ""456"", ""plan"": ""basic""}")!.AsObject();
            var cacheKeyAttributes = new[] { "userId" };

            // Should trigger when monitored attribute changes
            var result = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluation(
                oldAttributes, newAttributes, cacheKeyAttributes);
            result.Should().BeTrue();

            // Should not trigger when non-monitored attribute changes
            var newAttributes2 = JsonNode.Parse(@"{""userId"": ""123"", ""plan"": ""premium""}")!.AsObject();
            var result2 = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluation(
                oldAttributes, newAttributes2, cacheKeyAttributes);
            result2.Should().BeFalse();
        }

        [Fact]
        public void ShouldTriggerRemoteEvaluationForForcedVariations_ShouldDetectChanges()
        {
            var oldVariations = new Dictionary<string, int> { { "exp1", 1 } };
            var newVariations = new Dictionary<string, int> { { "exp1", 2 } };

            var result = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluationForForcedVariations(
                oldVariations, newVariations);
            result.Should().BeTrue();

            // Same variations should not trigger
            var result2 = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluationForForcedVariations(
                oldVariations, oldVariations);
            result2.Should().BeFalse();
        }

        [Fact]
        public void IsValidForRemoteEvaluation_ShouldValidateCorrectly()
        {
            // Valid context
            var validContext = new Context
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com"
            };
            RemoteEvaluationUtilities.IsValidForRemoteEvaluation(validContext).Should().BeTrue();

            // Invalid contexts
            var disabledContext = new Context { RemoteEval = false };
            RemoteEvaluationUtilities.IsValidForRemoteEvaluation(disabledContext).Should().BeFalse();

            var missingKeyContext = new Context { RemoteEval = true, ApiHost = "https://api.example.com" };
            RemoteEvaluationUtilities.IsValidForRemoteEvaluation(missingKeyContext).Should().BeFalse();

            var withDecryptionContext = new Context
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                DecryptionKey = "key"
            };
            RemoteEvaluationUtilities.IsValidForRemoteEvaluation(withDecryptionContext).Should().BeFalse();
        }
    }
}
