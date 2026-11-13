using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Forced feature values, as <c>forcedFeatureValues</c> in the JavaScript SDK: a forced feature is not evaluated,
/// it resolves to the given value with the <c>override</c> source.
/// </summary>
public class ForcedFeaturesTests
{
    private static Dictionary<string, Feature> Features() => new Dictionary<string, Feature>
    {
        ["banner"] = new Feature
        {
            DefaultValue = "default",
            Rules = new List<FeatureRule> { new FeatureRule { Force = "from-rule" } }
        },
        ["gated"] = new Feature
        {
            DefaultValue = "closed",
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Force = "open",
                    ParentConditions = new List<ParentCondition>
                    {
                        new ParentCondition { Id = "banner", Condition = JObject.Parse(@"{""value"": ""forced""}") }
                    }
                }
            }
        }
    };

    private static GrowthBook Create(IDictionary<string, JToken> forced = null) => new GrowthBook(new Context
    {
        Enabled = true,
        Attributes = new JObject { ["id"] = "u1" },
        Features = Features(),
        ForcedFeatures = forced,
        LoggerFactory = NullLoggerFactory.Instance
    });

    [Fact]
    public void AForcedFeatureResolvesToItsValueWithTheOverrideSource()
    {
        using var gb = Create(new Dictionary<string, JToken> { ["banner"] = "forced" });

        var result = gb.EvalFeature("banner");

        result.Value.ToString().Should().Be("forced");
        result.Source.Should().Be(FeatureResult.SourceId.Override);
        gb.GetFeatureValue("banner", "fallback").Should().Be("forced");
    }

    [Fact]
    public void AFeatureThePayloadDoesNotDefineCanStillBeForced()
    {
        using var gb = Create(new Dictionary<string, JToken> { ["not-in-payload"] = true });

        gb.IsOn("not-in-payload").Should().BeTrue();
        gb.EvalFeature("not-in-payload").Source.Should().Be(FeatureResult.SourceId.Override);
    }

    [Fact]
    public void FeaturesThatAreNotForcedAreEvaluatedAsBefore()
    {
        using var gb = Create(new Dictionary<string, JToken> { ["other"] = true });

        var result = gb.EvalFeature("banner");

        result.Value.ToString().Should().Be("from-rule");
        result.Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void AFeatureForcedToNullIsOffAndStillAnOverride()
    {
        using var gb = Create(new Dictionary<string, JToken> { ["banner"] = null });

        var result = gb.EvalFeature("banner");

        result.On.Should().BeFalse();
        result.Source.Should().Be(FeatureResult.SourceId.Override);
    }

    [Fact]
    public void APrerequisiteSeesTheForcedValue()
    {
        using var gb = Create();

        gb.EvalFeature("gated").Value.ToString().Should().Be("closed", "the parent resolves to its rule's value");

        gb.SetForcedFeatures(new Dictionary<string, JToken> { ["banner"] = "forced" });

        gb.EvalFeature("gated").Value.ToString().Should().Be("open", "the parent is forced to the value the rule asks for");
    }

    [Fact]
    public void SetForcedFeaturesReplacesWhatWasForced()
    {
        using var gb = Create(new Dictionary<string, JToken> { ["banner"] = "first" });

        gb.SetForcedFeatures(new Dictionary<string, JToken> { ["banner"] = "second" });
        gb.EvalFeature("banner").Value.ToString().Should().Be("second");
        gb.GetForcedFeatures().Should().ContainKey("banner");

        gb.SetForcedFeatures(null);
        gb.EvalFeature("banner").Source.Should().Be(FeatureResult.SourceId.Force);
        gb.GetForcedFeatures().Should().BeEmpty();
    }

    [Fact]
    public void ChangingWhatWasPassedInAfterwardsDoesNotChangeTheOverride()
    {
        var limits = JObject.Parse(@"{""max"": 5}");
        var forced = new Dictionary<string, JToken> { ["limits"] = limits };

        using var gb = Create();
        gb.SetForcedFeatures(forced);

        limits["max"] = 99;
        forced["banner"] = "added-later";

        gb.EvalFeature("limits").Value["max"].Value<int>().Should().Be(5);
        gb.EvalFeature("banner").Source.Should().Be(FeatureResult.SourceId.Force);
    }

    [Fact]
    public void ACloneOfTheContextHasItsOwnForcedValues()
    {
        var context = new Context { ForcedFeatures = new Dictionary<string, JToken> { ["banner"] = "forced" } };

        var clone = context.Clone();
        clone.ForcedFeatures["banner"] = "changed";
        clone.ForcedFeatures["extra"] = true;

        context.ForcedFeatures.Should().ContainSingle().Which.Value.ToString().Should().Be("forced");
    }

    [Fact]
    public async Task TheClientForcesFeaturesForEveryoneAndAUsersOwnValueWins()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(Features()));

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            GlobalForcedFeatures = new Dictionary<string, JToken> { ["banner"] = "for-everyone" }
        });

        await client.InitializeAsync();

        var plainUser = new UserContext { Attributes = new JObject { ["id"] = "u1" } };
        var overridingUser = new UserContext
        {
            Attributes = new JObject { ["id"] = "u2" },
            ForcedFeatures = new Dictionary<string, JToken> { ["banner"] = "for-this-user" }
        };

        var global = client.EvalFeature("banner", plainUser);
        global.Value.ToString().Should().Be("for-everyone");
        global.Source.Should().Be(FeatureResult.SourceId.Override);

        client.EvalFeature("banner", overridingUser).Value.ToString().Should().Be("for-this-user");

        client.SetGlobalForcedFeatures(null);

        client.EvalFeature("banner", plainUser).Source.Should().Be(FeatureResult.SourceId.Force);
    }

    /// <summary>
    /// The server can only apply forced values that reach the request. They used to be left out of the state a
    /// remote evaluation is made from, so the request always carried an empty list.
    /// </summary>
    [Fact]
    public async Task ForcedFeaturesReachTheRemoteEvaluationRequest()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository
            .GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(Features()));

        using var gb = new GrowthBook(new Context
        {
            RemoteEval = true,
            ClientKey = "test-key",
            ApiHost = "https://api.example.com",
            ForcedFeatures = new Dictionary<string, JToken> { ["dark-mode"] = true },
            FeatureRepository = repository,
            LoggerFactory = NullLoggerFactory.Instance
        });

        await gb.LoadFeaturesWithResult();

        await repository.Received(1).GetFeaturesWithContext(
            Arg.Is<Context>(x => x.ForcedFeatures.ContainsKey("dark-mode") && x.ForcedFeatures["dark-mode"].Value<bool>()),
            Arg.Any<GrowthBookRetrievalOptions>(),
            Arg.Any<CancellationToken?>());
    }

    /// <summary>
    /// The endpoint takes pairs of key and value, as <c>Array.from(getForcedFeatures().entries())</c> in the
    /// JavaScript SDK. Every JSON type has to arrive as the second item of its pair, null included: forcing a
    /// feature to null is an override, not the absence of one.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("3.5")]
    [InlineData("\"hello\"")]
    [InlineData("{\"max\":5,\"nested\":{\"deep\":true}}")]
    [InlineData("[1,2,3]")]
    public void TheRequestCarriesEachForcedValueAsTheSecondItemOfItsPair(string valueJson)
    {
        var context = new Context
        {
            ForcedFeatures = new Dictionary<string, JToken> { ["dark-mode"] = JToken.Parse(valueJson) }
        };

        var json = JsonConvert.SerializeObject(RemoteEvaluationRequest.FromContext(context));

        json.Should().Contain($"\"forcedFeatures\":[[\"dark-mode\",{valueJson}]]");
    }

    [Fact]
    public void AFeatureForcedToANullReferenceIsStillSentAsNull()
    {
        var context = new Context { ForcedFeatures = new Dictionary<string, JToken> { ["dark-mode"] = null } };

        var json = JsonConvert.SerializeObject(RemoteEvaluationRequest.FromContext(context));

        json.Should().Contain("\"forcedFeatures\":[[\"dark-mode\",null]]");
    }
}
