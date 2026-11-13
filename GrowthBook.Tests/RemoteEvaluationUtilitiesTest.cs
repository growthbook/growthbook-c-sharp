using System.Collections.Generic;
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
                Attributes = JObject.Parse(@"{""userId"": ""123"", ""plan"": ""basic""}"),
                CacheKeyAttributes = new[] { "userId" }
            };

            var result = RemoteEvaluationUtilities.GenerateCacheKey(context);

            result.Should().NotBeNull();
            result.Should().StartWith("https://api.example.com||test-key||");
            result.Should().Contain("userId");
            result.Should().NotContain("plan"); // Should be filtered out
        }

        /// <summary>
        /// The server evaluates with the forced values, so a cache keyed without them returned the response made for
        /// the old override after it changed or was cleared.
        /// </summary>
        [Fact]
        public void GenerateCacheKey_ChangesWhenAForcedFeatureIsSetChangedOrCleared()
        {
            string KeyFor(Dictionary<string, JToken> forced) => RemoteEvaluationUtilities.GenerateCacheKey(new Context
            {
                RemoteEval = true,
                ApiHost = "https://api.example.com",
                ClientKey = "test-key",
                Attributes = JObject.Parse(@"{""userId"": ""123""}"),
                ForcedFeatures = forced
            });

            var none = KeyFor(null);
            var on = KeyFor(new Dictionary<string, JToken> { ["dark-mode"] = true });
            var off = KeyFor(new Dictionary<string, JToken> { ["dark-mode"] = false });
            var forcedToNull = KeyFor(new Dictionary<string, JToken> { ["dark-mode"] = JValue.CreateNull() });

            new[] { none, on, off, forcedToNull }.Should().OnlyHaveUniqueItems();
            KeyFor(new Dictionary<string, JToken>()).Should().Be(none, "clearing the overrides returns to the key with none");
        }

        [Fact]
        public void GenerateCacheKey_IsUnchangedWhenNothingIsForced()
        {
            var context = new Context
            {
                RemoteEval = true,
                ApiHost = "https://api.example.com",
                ClientKey = "test-key",
                Attributes = JObject.Parse(@"{""userId"": ""123""}")
            };

            RemoteEvaluationUtilities.GenerateCacheKey(context)
                .Should().Be(@"https://api.example.com||test-key||{""attributes"":{""userId"":""123""}}", "keys a cache already holds stay valid");
        }

        [Fact]
        public void GenerateCacheKey_DoesNotDependOnTheOrderForcedFeaturesWereAdded()
        {
            string KeyFor(Dictionary<string, JToken> forced) => RemoteEvaluationUtilities.GenerateCacheKey(new Context
            {
                RemoteEval = true,
                ApiHost = "https://api.example.com",
                ClientKey = "test-key",
                ForcedFeatures = forced
            });

            KeyFor(new Dictionary<string, JToken> { ["a"] = 1, ["b"] = 2 })
                .Should().Be(KeyFor(new Dictionary<string, JToken> { ["b"] = 2, ["a"] = 1 }));
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
            var oldAttributes = JObject.Parse(@"{""userId"": ""123"", ""plan"": ""basic""}");
            var newAttributes = JObject.Parse(@"{""userId"": ""456"", ""plan"": ""basic""}");
            var cacheKeyAttributes = new[] { "userId" };

            // Should trigger when monitored attribute changes
            var result = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluation(
                oldAttributes, newAttributes, cacheKeyAttributes);
            result.Should().BeTrue();

            // Should not trigger when non-monitored attribute changes
            var newAttributes2 = JObject.Parse(@"{""userId"": ""123"", ""plan"": ""premium""}");
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
