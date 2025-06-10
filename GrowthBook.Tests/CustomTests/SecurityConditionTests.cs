using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for security-critical scenarios where null/missing attributes 
    /// should not satisfy comparison operators.
    /// </summary>
    public class SecurityConditionTests
    {
        private readonly ConditionEvaluationProvider _provider;

        public SecurityConditionTests()
        {
            var logger = new NullLogger<ConditionEvaluationProvider>();
            _provider = new ConditionEvaluationProvider(logger);
        }

        [Fact]
        public void SecurityTest_MissingUserLevel_ShouldDenyAccess()
        {
            // Access control based on user level
            var condition = JObject.Parse(@"{
                ""userLevel"": { ""$gte"": 5 }
            }");
            
            var attributesWithoutUserLevel = JObject.Parse("{}");
            
            var result = _provider.EvalCondition(attributesWithoutUserLevel, condition);
            
            result.Should().BeFalse("because missing userLevel should deny access");
        }

        [Fact]
        public void SecurityTest_NullUserLevel_ShouldDenyAccess()
        {
            // Access control based on user level
            var condition = JObject.Parse(@"{
                ""userLevel"": { ""$gte"": 5 }
            }");
            
            var attributesWithNullUserLevel = JObject.Parse(@"{
                ""userLevel"": null
            }");
            
            var result = _provider.EvalCondition(attributesWithNullUserLevel, condition);
            
            result.Should().BeFalse("because null userLevel should deny access");
        }

        [Fact]
        public void SecurityTest_MissingAge_ShouldBlockContentAccess()
        {
            // Age verification for content
            var condition = JObject.Parse(@"{
                ""age"": { ""$gte"": 18 }
            }");
            
            var attributesWithoutAge = JObject.Parse("{}");
            
            var result = _provider.EvalCondition(attributesWithoutAge, condition);
            
            result.Should().BeFalse("because missing age should block access to 18+ content");
        }

        [Fact]
        public void SecurityTest_NullCreditScore_ShouldRejectTransaction()
        {
            // Credit limit checks
            var condition = JObject.Parse(@"{
                ""creditScore"": { ""$gt"": 600 }
            }");
            
            var attributesWithNullCreditScore = JObject.Parse(@"{
                ""creditScore"": null
            }");
            
            var result = _provider.EvalCondition(attributesWithNullCreditScore, condition);
            
            result.Should().BeFalse("because null creditScore should reject transaction");
        }

        [Fact]
        public void SecurityTest_MissingMemory_ShouldDenyResourceAllocation()
        {
            // Resource allocation
            var condition = JObject.Parse(@"{
                ""availableMemory"": { ""$gte"": 1024 }
            }");
            
            var attributesWithoutMemory = JObject.Parse("{}");
            
            var result = _provider.EvalCondition(attributesWithoutMemory, condition);
            
            result.Should().BeFalse("because missing availableMemory should deny resource allocation");
        }

        [Fact]
        public void SecurityTest_NullDataQuality_ShouldRejectData()
        {
            // Data validation pipeline
            var condition = JObject.Parse(@"{
                ""dataQuality"": { ""$gt"": 0.8 }
            }");
            
            var attributesWithNullQuality = JObject.Parse(@"{
                ""dataQuality"": null
            }");
            
            var result = _provider.EvalCondition(attributesWithNullQuality, condition);
            
            result.Should().BeFalse("because null dataQuality should reject data processing");
        }

        [Fact]
        public void SecurityTest_ValidAttributes_ShouldAllowAccess()
        {
            // Positive test - valid attributes should work
            var condition = JObject.Parse(@"{
                ""userLevel"": { ""$gte"": 5 },
                ""age"": { ""$gte"": 18 },
                ""creditScore"": { ""$gt"": 600 }
            }");
            
            var validAttributes = JObject.Parse(@"{
                ""userLevel"": 10,
                ""age"": 25,
                ""creditScore"": 750
            }");
            
            var result = _provider.EvalCondition(validAttributes, condition);
            
            result.Should().BeTrue("because all valid attributes should satisfy the conditions");
        }

        [Fact]
        public void SecurityTest_MixedNullAndValid_ShouldDenyAccess()
        {
            // Mixed scenario - some valid, some null
            var condition = JObject.Parse(@"{
                ""userLevel"": { ""$gte"": 5 },
                ""age"": { ""$gte"": 18 },
                ""creditScore"": { ""$gt"": 600 }
            }");
            
            var mixedAttributes = JObject.Parse(@"{
                ""userLevel"": 10,
                ""age"": 25,
                ""creditScore"": null
            }");
            
            var result = _provider.EvalCondition(mixedAttributes, condition);
            
            result.Should().BeFalse("because any null attribute should fail the entire condition");
        }

        [Fact]
        public void SecurityTest_AllComparisonOperators_WithNull_ShouldReturnFalse()
        {
            // Test all comparison operators with null values
            var conditions = new[]
            {
                @"{ ""value"": { ""$gt"": 10 } }",
                @"{ ""value"": { ""$gte"": 10 } }",
                @"{ ""value"": { ""$lt"": 10 } }",
                @"{ ""value"": { ""$lte"": 10 } }"
            };

            var nullAttributes = JObject.Parse(@"{ ""value"": null }");
            var missingAttributes = JObject.Parse("{}");

            foreach (var conditionJson in conditions)
            {
                var condition = JObject.Parse(conditionJson);
                
                var nullResult = _provider.EvalCondition(nullAttributes, condition);
                var missingResult = _provider.EvalCondition(missingAttributes, condition);
                
                nullResult.Should().BeFalse($"because null value should fail condition: {conditionJson}");
                missingResult.Should().BeFalse($"because missing value should fail condition: {conditionJson}");
            }
        }
    }
} 