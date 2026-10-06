using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for the $inGroup / $notInGroup saved group operators, with emphasis on the inputs the
    /// conformance fixtures don't reach. Every $inGroup case in standard-cases.json supplies the hash
    /// attribute, so the absent-attribute path is invisible to the spec suite and has to be pinned here.
    /// The reference behavior these assert against is the JS SDK's evalOperatorCondition:
    /// <code>
    /// const values = getSavedGroupArrayValues(savedGroups[expected]);
    /// return values === null ? false : isIn(actual, values);
    /// </code>
    /// Two things follow from that and are easy to get wrong. The null check sits outside the
    /// negation, so an unresolvable group makes both operators false rather than $notInGroup true.
    /// And the attribute reaches isIn through <c>getPath</c>, which returns null both for a key that
    /// is absent and for one set to null, so the two cannot be told apart here.
    /// </summary>
    public class SavedGroupOperatorTests
    {
        private const string GroupName = "admins";

        private readonly ConditionEvaluationProvider _provider;

        public SavedGroupOperatorTests()
        {
            var logger = new NullLogger<ConditionEvaluationProvider>();
            _provider = new ConditionEvaluationProvider(logger);
        }

        private static JObject SavedGroups => JObject.Parse(@"{ ""admins"": [ ""user-1"", ""user-2"" ] }");

        private static JObject InGroup(string groupId) => JObject.Parse($@"{{ ""id"": {{ ""$inGroup"": {groupId} }} }}");

        private static JObject NotInGroup(string groupId) => JObject.Parse($@"{{ ""id"": {{ ""$notInGroup"": {groupId} }} }}");

        private bool Eval(JObject attributes, JObject condition) => _provider.EvalCondition(attributes, condition, SavedGroups);

        private bool Eval(JObject attributes, JObject condition, string savedGroups) =>
            _provider.EvalCondition(attributes, condition, JObject.Parse(savedGroups));

        [Fact]
        public void InGroupMatchesAMemberOfTheGroup()
        {
            var attributes = JObject.Parse(@"{ ""id"": ""user-1"" }");

            Eval(attributes, InGroup($"\"{GroupName}\"")).Should().BeTrue("because user-1 is listed in the group");
        }

        [Fact]
        public void InGroupDoesNotMatchANonMember()
        {
            var attributes = JObject.Parse(@"{ ""id"": ""user-9"" }");

            Eval(attributes, InGroup($"\"{GroupName}\"")).Should().BeFalse("because user-9 is not listed in the group");
        }

        [Fact]
        public void NotInGroupIsTheExactNegationForAPresentAttribute()
        {
            var member = JObject.Parse(@"{ ""id"": ""user-1"" }");
            var nonMember = JObject.Parse(@"{ ""id"": ""user-9"" }");

            Eval(member, NotInGroup($"\"{GroupName}\"")).Should().BeFalse();
            Eval(nonMember, NotInGroup($"\"{GroupName}\"")).Should().BeTrue();
        }

        [Fact]
        public void NotInGroupIsTrueWhenTheAttributeIsAbsent()
        {
            var attributes = JObject.Parse(@"{ ""other"": ""value"" }");

            Eval(attributes, NotInGroup($"\"{GroupName}\""))
                .Should().BeTrue("because the reference SDK evaluates !isIn(undefined, [...]) as true - an absent attribute is not a member of anything");
        }

        [Fact]
        public void InGroupIsFalseWhenTheAttributeIsAbsent()
        {
            var attributes = JObject.Parse(@"{ ""other"": ""value"" }");

            Eval(attributes, InGroup($"\"{GroupName}\""))
                .Should().BeFalse("because an absent attribute cannot be a member of the group");
        }

        /// <summary>
        /// Only while the group resolves. A group that doesn't is the one case where the reference
        /// makes both false on purpose, which <see cref="AnUnresolvableSavedGroupMakesBothOperatorsFalse"/>
        /// covers.
        /// </summary>
        [Fact]
        public void TheTwoOperatorsNeverAgreeForAnAbsentAttributeWhenTheGroupResolves()
        {
            var attributes = JObject.Parse(@"{ ""other"": ""value"" }");

            var isInGroup = Eval(attributes, InGroup($"\"{GroupName}\""));
            var isNotInGroup = Eval(attributes, NotInGroup($"\"{GroupName}\""));

            isInGroup.Should().NotBe(isNotInGroup,
                "because the operators are logical negations of each other - collapsing both to false asserts that the user is simultaneously in and not in the group");
        }

        /// <summary>
        /// <c>getPath</c> returns null for a key that is absent and for one set to null alike, so the
        /// two reach the operator as the same value and can never diverge - not even for a group that
        /// lists a null member, where both of them match it.
        /// </summary>
        [Fact]
        public void AnAbsentAttributeBehavesTheSameAsAnExplicitJsonNull()
        {
            const string groupsWithNullMember = @"{ ""admins"": [ ""user-1"", null ] }";

            var absent = JObject.Parse(@"{ ""other"": ""value"" }");
            var explicitNull = JObject.Parse(@"{ ""id"": null }");

            Eval(absent, InGroup($"\"{GroupName}\"")).Should().Be(Eval(explicitNull, InGroup($"\"{GroupName}\"")),
                "because a JValue wrapping JSON null and a missing key are both 'no value' and must not diverge");
            Eval(absent, NotInGroup($"\"{GroupName}\"")).Should().Be(Eval(explicitNull, NotInGroup($"\"{GroupName}\"")));

            Eval(absent, InGroup($"\"{GroupName}\""), groupsWithNullMember)
                .Should().Be(Eval(explicitNull, InGroup($"\"{GroupName}\""), groupsWithNullMember));
            Eval(absent, NotInGroup($"\"{GroupName}\""), groupsWithNullMember)
                .Should().Be(Eval(explicitNull, NotInGroup($"\"{GroupName}\""), groupsWithNullMember));
        }

        [Fact]
        public void AnUnknownGroupIdBehavesAsAnEmptyGroup()
        {
            var attributes = JObject.Parse(@"{ ""id"": ""user-1"" }");

            Eval(attributes, InGroup("\"no-such-group\"")).Should().BeFalse();
            Eval(attributes, NotInGroup("\"no-such-group\"")).Should().BeTrue("because savedGroups[expected] || [] makes an unknown group an empty one");
        }

        [Fact]
        public void ANullGroupIdBehavesAsAnEmptyGroup()
        {
            var attributes = JObject.Parse(@"{ ""id"": ""user-1"" }");

            Eval(attributes, InGroup("null")).Should().BeFalse();
            Eval(attributes, NotInGroup("null")).Should().BeTrue("because the reference SDK has no guard on the group id either");
        }

        [Fact]
        public void AnAbsentAttributeCombinedWithAnUnknownGroupStaysConsistent()
        {
            var attributes = JObject.Parse(@"{ ""other"": ""value"" }");

            Eval(attributes, InGroup("\"no-such-group\"")).Should().BeFalse();
            Eval(attributes, NotInGroup("\"no-such-group\"")).Should().BeTrue();
        }

        [Fact]
        public void GroupOperatorsStillWorkWithoutAnySavedGroups()
        {
            var attributes = JObject.Parse(@"{ ""id"": ""user-1"" }");

            _provider.EvalCondition(attributes, InGroup($"\"{GroupName}\""), new JObject()).Should().BeFalse();
            _provider.EvalCondition(attributes, NotInGroup($"\"{GroupName}\""), new JObject())
                .Should().BeTrue("because an empty saved group set must not make the operator fall through to a blanket false");
        }

        [Fact]
        public void InGroupMatchesWhenTheAttributeIsAnArraySharingAMember()
        {
            var attributes = JObject.Parse(@"{ ""id"": [ ""user-9"", ""user-2"" ] }");

            Eval(attributes, InGroup($"\"{GroupName}\"")).Should().BeTrue("because the reference isIn treats an array attribute as matching on any shared element");
            Eval(attributes, NotInGroup($"\"{GroupName}\"")).Should().BeFalse();
        }

        /// <summary>
        /// A group listing a null member matches a user with no value for the attribute, because
        /// <c>getPath</c> hands the operator a null for an absent key and <c>[null].includes(null)</c>
        /// is true. Guarding the absent case away would make this SDK stricter than the reference, so
        /// the same targeting rule would resolve differently here than in JS.
        /// </summary>
        [Fact]
        public void AnAbsentAttributeMatchesANullMemberOfTheGroup()
        {
            const string groupsWithNullMember = @"{ ""admins"": [ ""user-1"", null ] }";
            var absent = JObject.Parse(@"{ ""other"": ""value"" }");

            Eval(absent, InGroup($"\"{GroupName}\""), groupsWithNullMember)
                .Should().BeTrue("because the reference compares the absent attribute's null against the member and finds it");
            Eval(absent, NotInGroup($"\"{GroupName}\""), groupsWithNullMember).Should().BeFalse();
        }

        [Fact]
        public void AnAttributeExplicitlySetToNullMatchesANullMemberOfTheGroup()
        {
            const string groupsWithNullMember = @"{ ""admins"": [ ""user-1"", null ] }";
            var explicitNull = JObject.Parse(@"{ ""id"": null }");

            Eval(explicitNull, InGroup($"\"{GroupName}\""), groupsWithNullMember)
                .Should().BeTrue("because the reference compares null to null and finds the member");
            Eval(explicitNull, NotInGroup($"\"{GroupName}\""), groupsWithNullMember).Should().BeFalse();
        }

        /// <summary>
        /// A group that is defined but isn't a usable list is malformed payload, not a reason to
        /// abandon the evaluation - but it is also not an empty group. The SDK cannot say whether a
        /// user belongs to a group it could not read, so both operators answer false. Negating the
        /// unresolvable case instead would have <c>$notInGroup</c> target everyone on a bad payload.
        /// </summary>
        [Theory]
        [InlineData(@"{ ""admins"": null }")]
        [InlineData(@"{ ""admins"": ""user-1"" }")]
        [InlineData(@"{ ""admins"": 42 }")]
        [InlineData(@"{ ""admins"": { ""user-1"": true } }")]
        [InlineData(@"{ ""admins"": { ""type"": ""list"", ""values"": ""user-1"" } }")]
        public void AnUnresolvableSavedGroupMakesBothOperatorsFalse(string savedGroups)
        {
            var present = JObject.Parse(@"{ ""id"": ""user-1"" }");
            var absent = JObject.Parse(@"{ ""other"": ""value"" }");

            Eval(present, InGroup($"\"{GroupName}\""), savedGroups).Should().BeFalse();
            Eval(present, NotInGroup($"\"{GroupName}\""), savedGroups).Should().BeFalse(
                "because the group could not be read, which is not the same as the user not being in it");
            Eval(absent, InGroup($"\"{GroupName}\""), savedGroups).Should().BeFalse();
            Eval(absent, NotInGroup($"\"{GroupName}\""), savedGroups).Should().BeFalse();
        }

        /// <summary>
        /// A group nobody defined is empty rather than unreadable, so a user is legitimately not in
        /// it and <c>$notInGroup</c> holds. This is the case that separates "no group" from
        /// <see cref="AnUnresolvableSavedGroupMakesBothOperatorsFalse"/>.
        /// </summary>
        [Fact]
        public void AGroupThatIsNotDefinedAtAllIsAnEmptyGroupRatherThanAnUnreadableOne()
        {
            var present = JObject.Parse(@"{ ""id"": ""user-1"" }");

            Eval(present, InGroup("\"nobody-defined-this\""), @"{ ""admins"": [ ""user-1"" ] }").Should().BeFalse();
            Eval(present, NotInGroup("\"nobody-defined-this\""), @"{ ""admins"": [ ""user-1"" ] }").Should().BeTrue(
                "because an undefined group is empty, and a user is genuinely not a member of an empty group");
        }

        /// <summary>
        /// Alongside a bare array, the reference accepts a group delivered as
        /// <c>{ type: "list", values: [...] }</c>. Reading only the array shape would silently treat
        /// every group sent this way as empty.
        /// </summary>
        [Fact]
        public void AListShapedSavedGroupIsReadLikeAPlainArray()
        {
            const string listShaped = @"{ ""admins"": { ""type"": ""list"", ""values"": [ ""user-1"" ] } }";

            var member = JObject.Parse(@"{ ""id"": ""user-1"" }");
            var nonMember = JObject.Parse(@"{ ""id"": ""user-9"" }");

            Eval(member, InGroup($"\"{GroupName}\""), listShaped).Should().BeTrue();
            Eval(member, NotInGroup($"\"{GroupName}\""), listShaped).Should().BeFalse();
            Eval(nonMember, InGroup($"\"{GroupName}\""), listShaped).Should().BeFalse();
            Eval(nonMember, NotInGroup($"\"{GroupName}\""), listShaped).Should().BeTrue();
        }
    }
}
