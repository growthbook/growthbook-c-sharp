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

        public static IEnumerable<object[]> NumberEquality() => new[]
        {
            // Above 2^53, distinct integers share a double, so integers compare exactly
            new object[] { "9007199254740993", "9007199254740992", false },
            new object[] { "9007199254740993", "9007199254740993", true },
            new object[] { "9223372036854775807", "9223372036854775806", false },
            new object[] { "9223372036854775807", "9223372036854775807", true },
            new object[] { "18446744073709551617", "18446744073709551616", false },
            new object[] { "18446744073709551617", "18446744073709551617", true },
            new object[] { "-9007199254740993", "-9007199254740992", false },
            new object[] { "1", "1.0", true },
            new object[] { "1.0", "1", true },
            new object[] { "0", "-0.0", true },
            new object[] { "1.5", "1.5", true },
            new object[] { "1", "1.5", false },
            new object[] { "4503599627370495.5", "4503599627370495", false },
            // A decimal compares as a double, as in JavaScript, where these are the same number
            new object[] { "9007199254740993", "9007199254740992.0", true }
        };

        [Theory]
        [MemberData(nameof(NumberEquality))]
        public void NumbersAreEqualOnlyWhenTheirValuesAre(string attribute, string condition, bool expected)
        {
            var attributes = JObject.Parse($"{{ \"v\": {attribute}, \"a\": [{attribute}], \"o\": {{ \"n\": [{attribute}] }} }}");

            Eval(attributes, $"{{ \"v\": {condition} }}").Should().Be(expected, "plain equality");
            Eval(attributes, $"{{ \"v\": {{ \"$eq\": {condition} }} }}").Should().Be(expected, "$eq");
            Eval(attributes, $"{{ \"v\": {{ \"$ne\": {condition} }} }}").Should().Be(!expected, "$ne");
            Eval(attributes, $"{{ \"v\": {{ \"$gte\": {condition}, \"$lte\": {condition} }} }}").Should().Be(expected, "$gte and $lte");
            Eval(attributes, $"{{ \"v\": {{ \"$in\": [{condition}] }} }}").Should().Be(expected, "scalar $in");
            Eval(attributes, $"{{ \"a\": [{condition}] }}").Should().Be(expected, "array equality");
            Eval(attributes, $"{{ \"o\": {{ \"n\": [{condition}] }} }}").Should().Be(expected, "nested equality");
            Eval(attributes, $"{{ \"a\": {{ \"$elemMatch\": {{ \"$eq\": {condition} }} }} }}").Should().Be(expected, "$elemMatch");

            foreach (var withDate in new[] { false, true })
            {
                var list = withDate ? $"[{DateElement}, {condition}]" : $"[{condition}]";
                var savedGroups = JObject.Parse($"{{ \"g\": {list} }}");

                Eval(attributes, $"{{ \"a\": {{ \"$in\": {list} }} }}").Should().Be(expected, "array $in, date in list: {0}", withDate);
                Eval(attributes, $"{{ \"a\": {{ \"$nin\": {list} }} }}").Should().Be(!expected, "array $nin, date in list: {0}", withDate);
                Eval(attributes, "{ \"a\": { \"$inGroup\": \"g\" } }", savedGroups).Should().Be(expected, "array $inGroup, date in list: {0}", withDate);
            }
        }

        [Fact]
        public void LargeIntegersOrderExactly()
        {
            var attributes = JObject.Parse("{ \"v\": 9007199254740993 }");

            Eval(attributes, "{ \"v\": { \"$gt\": 9007199254740992 } }").Should().BeTrue();
            Eval(attributes, "{ \"v\": { \"$lt\": 9007199254740994 } }").Should().BeTrue();
            Eval(attributes, "{ \"v\": { \"$gt\": 18446744073709551616 } }").Should().BeFalse();
            Eval(attributes, "{ \"v\": { \"$gt\": 9007199254740992.5 } }").Should().BeFalse("a decimal is compared as a double");
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
