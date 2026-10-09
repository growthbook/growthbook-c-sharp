using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Pins that a force rule combining <c>coverage</c> with <c>hashVersion</c> hashes against the version the
/// rule declares. The two headline cases are taken verbatim from the upstream spec suite at
/// <c>specVersion</c> 0.8.0, which our vendored fixture (0.7.1) does not carry - so nothing else in the
/// suite covers this, and a rollout shortcut that hardcoded version 1 would go unnoticed.
/// </summary>
public class ForceRuleHashVersionTests
{
    private const string FeatureKey = "feature";

    private static FeatureResult Evaluate(string userId, string ruleJson)
    {
        // Deserialized rather than constructed so the JSON defaults are exercised, which is how an unset
        // hashVersion reaches the evaluator in practice.
        var feature = JsonConvert.DeserializeObject<Feature>("{\"defaultValue\":0,\"rules\":[" + ruleJson + "]}");

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = userId }),
            Features = new Dictionary<string, Feature> { [FeatureKey] = feature }
        });

        return growthBook.EvalFeature(FeatureKey);
    }

    private const string CoverageHashVersion2 = "{\"force\":1,\"coverage\":0.5,\"hashVersion\":2}";
    private const string CoverageHashVersion1 = "{\"force\":1,\"coverage\":0.5,\"hashVersion\":1}";
    private const string CoverageNoHashVersion = "{\"force\":1,\"coverage\":0.5}";

    [Fact]
    public void HashVersion2IncludesTheUserItShould()
    {
        // Upstream: "force rules - hashVersion 2 includes user"
        var result = Evaluate("user2", CoverageHashVersion2);

        result.Value.Value<int>().Should().Be(1);
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void HashVersion2ExcludesAUserThatVersion1WouldHaveIncluded()
    {
        // Upstream: "force rules - hashVersion 2 excludes user that v1 would include". This is the case
        // that catches a hardcoded version - under v1 the same user is included.
        var result = Evaluate("user3", CoverageHashVersion2);

        result.Value.Value<int>().Should().Be(0);
        result.Source.Should().Be(FeatureResult.SourceId.DefaultValue);
    }

    [Fact]
    public void TheSameUserIsIncludedUnderVersion1()
    {
        var result = Evaluate("user3", CoverageHashVersion1);

        result.Value.Value<int>().Should().Be(1, "because the two versions genuinely disagree for this user - which is what makes the previous test meaningful");
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void AnUnsetHashVersionBehavesAsVersion1()
    {
        var result = Evaluate("user3", CoverageNoHashVersion);

        result.Value.Value<int>().Should().Be(1);
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void TheTwoVersionsBucketTheseTwoUsersInOppositeDirections()
    {
        // user2 and user3 swap inclusion between the versions, which is the strongest available signal
        // that the declared version is honoured: a hardcoded version would give both users the same
        // answer no matter what the rule says.
        Evaluate("user2", CoverageHashVersion2).Source.Should().Be(FeatureResult.SourceId.Force);
        Evaluate("user2", CoverageHashVersion1).Source.Should().Be(FeatureResult.SourceId.DefaultValue);

        Evaluate("user3", CoverageHashVersion2).Source.Should().Be(FeatureResult.SourceId.DefaultValue);
        Evaluate("user3", CoverageHashVersion1).Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void AForceRuleWithoutCoverageIsUnaffectedByHashVersion()
    {
        var withVersion = Evaluate("user3", "{\"force\":1,\"hashVersion\":2}");
        var withoutVersion = Evaluate("user3", "{\"force\":1}");

        withVersion.Value.Value<int>().Should().Be(1, "because with no coverage there is no inclusion hash to compute");
        withoutVersion.Value.Value<int>().Should().Be(1);
        withVersion.Source.Should().Be(withoutVersion.Source);
    }
}
