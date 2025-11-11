using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class RegexNotMatchTests
    {
        private readonly ConditionEvaluationProvider _provider;

        public RegexNotMatchTests()
        {
            var logger = new NullLogger<ConditionEvaluationProvider>();
            _provider = new ConditionEvaluationProvider(logger);
        }

        [Fact]
        public void Regex_Should_Match_When_Pattern_Found()
        {
            // Test case from GitHub issue: attributes like "FR", "FR,LO", "FR,LO,W6"
            var attributes = JsonSerializer.SerializeToNode(new { region = "FR,LO,W6" })!.AsObject();

            var condition = JsonNode.Parse(@"{
                ""region"": {
                    ""$regex"": "".*(FR|W6).*""
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeTrue("because FR,LO,W6 should match the regex .*(FR|W6).*");
        }

        [Fact]
        public void Not_Regex_Should_Not_Match_When_Pattern_Found()
        {
            // Test "does not match regex" functionality
            var attributes = JsonSerializer.SerializeToNode(new { region = "FR,LO,W6" })!.AsObject();
            var condition = JsonNode.Parse(@"{
                ""region"": {
                    ""$not"": {
                        ""$regex"": "".*(FR|W6).*""
                    }
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeFalse("because FR,LO,W6 matches the regex, so $not should return false");
        }

        [Fact]
        public void Not_Regex_Should_Match_When_Pattern_Not_Found()
        {
            // Test "does not match regex" with non-matching value
            var attributes = JsonSerializer.SerializeToNode(new { region = "US,CA" })!.AsObject();

            var condition = JsonNode.Parse(@"{
                ""region"": {
                    ""$not"": {
                        ""$regex"": "".*(FR|W6).*""
                    }
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeTrue("because US,CA does not match the regex, so $not should return true");
        }

        [Fact]
        public void Standard_Test_Case_Not_Regex_Pass()
        {
            // From standard-cases.json: "$not - pass"
            var attributes = JsonSerializer.SerializeToNode(new { name = "world" })!.AsObject();
            var condition = JsonNode.Parse(@"{
                ""name"": {
                    ""$not"": {
                        ""$regex"": ""^hello""
                    }
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeTrue("because 'world' does not start with 'hello'");
        }

        [Fact]
        public void Standard_Test_Case_Not_Regex_Fail()
        {
            // From standard-cases.json: "$not - fail"  
            var attributes = JsonSerializer.SerializeToNode(new { name = "hello world" })!.AsObject();
            var condition = JsonNode.Parse(@"{
                ""name"": {
                    ""$not"": {
                        ""$regex"": ""^hello""
                    }
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeFalse("because 'hello world' starts with 'hello'");
        }

        [Fact]
        public void NRegex_Should_Not_Match_When_Pattern_Found()
        {
            // Test new $nregex operator (negative regex)
            var attributes = JsonSerializer.SerializeToNode(new { region = "FR,LO,W6" })!.AsObject();

            var condition = JsonNode.Parse(@"{
                ""region"": {
                    ""$nregex"": "".*(FR|W6).*""
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeFalse("because FR,LO,W6 matches the regex, so $nregex should return false");
        }

        [Fact]
        public void NRegex_Should_Match_When_Pattern_Not_Found()
        {
            // Test $nregex with non-matching value
            var attributes = JsonSerializer.SerializeToNode(new { region = "US,CA" })!.AsObject();

            var condition = JsonNode.Parse(@"{
                ""region"": {
                    ""$nregex"": "".*(FR|W6).*""
                }
            }");

            var result = _provider.EvalCondition(attributes, condition);

            result.Should().BeTrue("because US,CA does not match the regex, so $nregex should return true");
        }
    }
}