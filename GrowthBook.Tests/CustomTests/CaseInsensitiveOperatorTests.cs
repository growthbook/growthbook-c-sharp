using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers case-insensitive operator behavior that standard-cases.json does not exercise. Every
/// $alli fixture in the shared spec suite uses plain string elements only, so an implementation
/// that degraded $alli to simple string equality would still pass the whole spec suite. The
/// reference SDK routes $alli elements through evalConditionValue (same as $all), which means
/// operator objects and non-string condition values have to keep working.
/// </summary>
public class CaseInsensitiveOperatorTests : UnitTest
{
    private static bool Eval(string condition, string attributes)
    {
        var provider = new ConditionEvaluationProvider(new NullLogger<ConditionEvaluationProvider>());

        return provider.EvalCondition(JObject.Parse(attributes), JObject.Parse(condition), new JObject());
    }

    [Fact]
    public void AlliSupportsOperatorObjectElements()
    {
        Eval(@"{""nums"": {""$alli"": [{""$gt"": 5}]}}", @"{""nums"": [3, 10]}")
            .Should().BeTrue("because one element is greater than 5, and $alli must still evaluate operator objects");

        Eval(@"{""nums"": {""$alli"": [{""$gt"": 50}]}}", @"{""nums"": [3, 10]}")
            .Should().BeFalse("because no element is greater than 50");
    }

    [Fact]
    public void AlliSupportsNumericElements()
    {
        Eval(@"{""nums"": {""$alli"": [5, 6]}}", @"{""nums"": [5, 6, 7]}")
            .Should().BeTrue("because both required numbers are present");

        Eval(@"{""nums"": {""$alli"": [5, 99]}}", @"{""nums"": [5, 6, 7]}")
            .Should().BeFalse("because 99 is missing");
    }

    [Fact]
    public void AlliSupportsBooleanAndNullElements()
    {
        Eval(@"{""flags"": {""$alli"": [true]}}", @"{""flags"": [true, false]}")
            .Should().BeTrue();

        Eval(@"{""flags"": {""$alli"": [null]}}", @"{""flags"": [null, ""x""]}")
            .Should().BeTrue();

        Eval(@"{""flags"": {""$alli"": [true]}}", @"{""flags"": [false]}")
            .Should().BeFalse();
    }

    [Fact]
    public void AlliStillMatchesStringsCaseInsensitively()
    {
        Eval(@"{""tags"": {""$alli"": [""one"", ""three""]}}", @"{""tags"": [""ONE"", ""two"", ""THREE""]}")
            .Should().BeTrue("because this is the behavior the spec fixtures already pin");

        Eval(@"{""tags"": {""$alli"": [""one"", ""four""]}}", @"{""tags"": [""ONE"", ""two""]}")
            .Should().BeFalse();
    }

    [Fact]
    public void AllRemainsCaseSensitive()
    {
        Eval(@"{""tags"": {""$all"": [""one""]}}", @"{""tags"": [""ONE""]}")
            .Should().BeFalse("because $all must stay case-sensitive");

        Eval(@"{""tags"": {""$all"": [""ONE""]}}", @"{""tags"": [""ONE""]}")
            .Should().BeTrue();
    }

    [Fact]
    public void AllStillSupportsOperatorObjectElements()
    {
        Eval(@"{""nums"": {""$all"": [{""$gte"": 7}]}}", @"{""nums"": [3, 7]}")
            .Should().BeTrue("because the case-sensitive path must keep working after sharing code with $alli");
    }

    [Fact]
    public void IniAndNiniMatchStringsCaseInsensitivelyAndLeaveOtherTypesAlone()
    {
        Eval(@"{""country"": {""$ini"": [""usa"", ""canada""]}}", @"{""country"": ""USA""}")
            .Should().BeTrue();

        Eval(@"{""country"": {""$nini"": [""usa"", ""canada""]}}", @"{""country"": ""USA""}")
            .Should().BeFalse();

        Eval(@"{""num"": {""$ini"": [5]}}", @"{""num"": 5}")
            .Should().BeTrue("because non-string values compare normally");

        Eval(@"{""num"": {""$ini"": [""5""]}}", @"{""num"": 5}")
            .Should().BeFalse("because a string condition must not match a numeric attribute");
    }

    [Fact]
    public void IniAndNiniRejectNonArrayConditionValues()
    {
        Eval(@"{""country"": {""$ini"": ""usa""}}", @"{""country"": ""USA""}")
            .Should().BeFalse("because $ini requires an array condition value");

        Eval(@"{""country"": {""$nini"": ""usa""}}", @"{""country"": ""USA""}")
            .Should().BeFalse("because $nini returns false - not negated-true - for a non-array condition value");
    }

    [Fact]
    public void RegexiMatchesCaseInsensitivelyAndRegexStaysSensitive()
    {
        Eval(@"{""name"": {""$regexi"": ""^hello""}}", @"{""name"": ""HELLO world""}")
            .Should().BeTrue();

        Eval(@"{""name"": {""$regex"": ""^hello""}}", @"{""name"": ""HELLO world""}")
            .Should().BeFalse("because $regex must stay case-sensitive");
    }
}
