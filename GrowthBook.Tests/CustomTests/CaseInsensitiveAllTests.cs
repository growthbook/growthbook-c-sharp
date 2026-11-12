using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// <c>$alli</c> evaluates each item the way <c>$all</c> does, ignoring case only where a string is compared with a
    /// string, as <c>isInAll</c> in the JavaScript SDK. It used to compare each item for plain equality, so an
    /// operator inside it was compared against the attribute as an object and never matched.
    /// </summary>
    public class CaseInsensitiveAllTests
    {
        private readonly ConditionEvaluationProvider _provider = new ConditionEvaluationProvider(new NullLogger<ConditionEvaluationProvider>());

        private bool Matches(string tags, string condition) =>
            _provider.EvalCondition(JObject.Parse($"{{\"tags\": {tags}}}"), JObject.Parse($"{{\"tags\": {{\"$alli\": {condition}}}}}"));

        [Theory]
        [InlineData("[\"Beta\", \"pro\"]", "[{\"$regex\": \"^bet\"}]", false)]
        [InlineData("[\"beta\", \"pro\"]", "[{\"$regex\": \"^bet\"}]", true)]
        [InlineData("[3, 10]", "[{\"$gt\": 5}]", true)]
        [InlineData("[3, 4]", "[{\"$gt\": 5}]", false)]
        [InlineData("[\"BETA\", 10]", "[\"beta\", {\"$gte\": 10}]", true)]
        [InlineData("[\"BETA\", 9]", "[\"beta\", {\"$gte\": 10}]", false)]
        public void AnOperatorInsideIsEvaluated(string tags, string condition, bool expected)
        {
            Matches(tags, condition).Should().Be(expected);
        }

        [Theory]
        [InlineData("[\"One\", \"THREE\"]", "[\"one\", \"three\"]", true)]
        [InlineData("[\"one\"]", "[\"ONE\", \"three\"]", false)]
        public void StringsStillCompareWithoutCase(string tags, string condition, bool expected)
        {
            Matches(tags, condition).Should().Be(expected);
        }

        /// <summary>
        /// Case is ignored only between two strings. A string never matches a number, as in the JavaScript SDK, where
        /// a type mismatch fails before case is considered.
        /// </summary>
        [Theory]
        [InlineData("[5]", "[\"5\"]")]
        [InlineData("[\"5\"]", "[5]")]
        [InlineData("[true]", "[\"TRUE\"]")]
        public void AStringNeverMatchesAnotherType(string tags, string condition)
        {
            Matches(tags, condition).Should().BeFalse();
        }

        [Fact]
        public void TheSameConditionsUnderAllMatchExactly()
        {
            _provider.EvalCondition(JObject.Parse("{\"tags\": [\"BETA\", 10]}"), JObject.Parse("{\"tags\": {\"$all\": [\"beta\", {\"$gte\": 10}]}}"))
                .Should().BeFalse("$all compares strings with case");
            _provider.EvalCondition(JObject.Parse("{\"tags\": [\"beta\", 10]}"), JObject.Parse("{\"tags\": {\"$all\": [\"beta\", {\"$gte\": 10}]}}"))
                .Should().BeTrue();
        }
    }
}
