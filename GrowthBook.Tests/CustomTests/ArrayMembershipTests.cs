using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Array attributes checked with <c>$in</c>, <c>$nin</c>, <c>$inGroup</c> and <c>$notInGroup</c>. Lists of
    /// strings, numbers, booleans and nulls use a hash lookup; lists holding a timestamp Json.NET read as a date
    /// are compared pair by pair. Both must give the same, strictly typed, answers.
    /// </summary>
    public class ArrayMembershipTests
    {
        private readonly ConditionEvaluationProvider _provider;

        public ArrayMembershipTests()
        {
            _provider = new ConditionEvaluationProvider(new NullLogger<ConditionEvaluationProvider>());
        }

        // A date token in the list makes the evaluator compare pair by pair
        private const string DateElement = "\"2001-01-01T00:00:00Z\"";

        public static IEnumerable<object[]> StrictMembership() => new[]
        {
            new object[] { "[\"1\"]", "[1]", false },
            new object[] { "[1]", "[\"1\"]", false },
            new object[] { "[true]", "[1]", false },
            new object[] { "[1]", "[true]", false },
            new object[] { "[false]", "[0]", false },
            new object[] { "[null]", "[0]", false },
            new object[] { "[null]", "[\"\"]", false },
            new object[] { "[1]", "[1.0]", true },
            new object[] { "[1.0]", "[1]", true },
            new object[] { "[0]", "[-0.0]", true },
            new object[] { "[\"a\", 2]", "[\"b\", 2]", true },
            new object[] { "[true]", "[true]", true },
            new object[] { "[null]", "[null]", true },
            new object[] { "[\"A\"]", "[\"a\"]", false }
        };

        [Theory]
        [MemberData(nameof(StrictMembership))]
        public void ArrayMembershipIsStrictlyTyped(string attribute, string list, bool expected)
        {
            foreach (var withDate in new[] { false, true })
            {
                var conditionList = withDate ? list.Insert(1, DateElement + ", ") : list;
                var attributes = JObject.Parse($"{{ \"v\": {attribute} }}");
                var savedGroups = JObject.Parse($"{{ \"g\": {conditionList} }}");

                Eval(attributes, $"{{ \"v\": {{ \"$in\": {conditionList} }} }}", savedGroups).Should().Be(expected, "$in, date in list: {0}", withDate);
                Eval(attributes, $"{{ \"v\": {{ \"$nin\": {conditionList} }} }}", savedGroups).Should().Be(!expected, "$nin, date in list: {0}", withDate);
                Eval(attributes, "{ \"v\": { \"$inGroup\": \"g\" } }", savedGroups).Should().Be(expected, "$inGroup, date in list: {0}", withDate);
                Eval(attributes, "{ \"v\": { \"$notInGroup\": \"g\" } }", savedGroups).Should().Be(!expected, "$notInGroup, date in list: {0}", withDate);
            }
        }

        [Fact]
        public void CaseInsensitiveMembershipIgnoresCaseOnly()
        {
            var attributes = JObject.Parse("{ \"v\": [\"A\", \"1\"] }");

            Eval(attributes, "{ \"v\": { \"$ini\": [\"a\"] } }").Should().BeTrue();
            Eval(attributes, "{ \"v\": { \"$nini\": [\"a\"] } }").Should().BeFalse();
            Eval(attributes, "{ \"v\": { \"$ini\": [1] } }").Should().BeFalse();
        }

        [Fact]
        public void AnArrayOfObjectsStillMatchesByValue()
        {
            var attributes = JObject.Parse("{ \"v\": [{ \"id\": 1 }] }");

            Eval(attributes, "{ \"v\": { \"$in\": [{ \"id\": 1.0 }] } }").Should().BeTrue();
            Eval(attributes, "{ \"v\": { \"$in\": [{ \"id\": \"1\" }] } }").Should().BeFalse();
        }

        [Fact]
        public void ALargeArrayAgainstALargeSavedGroupIsCheckedWithoutComparingEveryPair()
        {
            // 2,000 × 50,000 values with no match is 100 million pairs, which takes many seconds pair by pair
            var attributes = new JObject { ["ids"] = new JArray(Enumerable.Range(0, 2_000).Select(i => $"user-{i}")) };
            var savedGroups = new JObject { ["big"] = new JArray(Enumerable.Range(100_000, 50_000).Select(i => $"user-{i}")) };
            var inGroup = JObject.Parse("{ \"ids\": { \"$inGroup\": \"big\" } }");
            var notInGroup = JObject.Parse("{ \"ids\": { \"$notInGroup\": \"big\" } }");

            var stopwatch = Stopwatch.StartNew();

            _provider.EvalCondition(attributes, inGroup, savedGroups).Should().BeFalse();
            _provider.EvalCondition(attributes, notInGroup, savedGroups).Should().BeTrue();

            stopwatch.Stop();
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(2_000, "the lists should be checked with a hash lookup");

            attributes["ids"].Last.Replace("user-149999");
            _provider.EvalCondition(attributes, inGroup, savedGroups).Should().BeTrue();
        }

        private bool Eval(JObject attributes, string condition, JObject savedGroups = null) =>
            _provider.EvalCondition(attributes, JObject.Parse(condition), savedGroups);
    }
}
