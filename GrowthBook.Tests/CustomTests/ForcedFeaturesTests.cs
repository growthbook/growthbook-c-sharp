using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class ForcedFeaturesTests : UnitTest
{
    [Fact]
    public void ForcedFeatureOverridesRulesAndDefaultValue()
    {
        const string FeatureName = "checkout-flow";

        var feature = new Feature
        {
            DefaultValue = false,
            Rules = new List<FeatureRule> { new() { Force = false } }
        };

        var context = new Context
        {
            Features = new Dictionary<string, Feature> { [FeatureName] = feature },
            ForcedFeatures = new Dictionary<string, JToken> { [FeatureName] = true }
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Source.Should().Be(FeatureResult.SourceId.Override, "because a forced feature value was configured");
        result.On.Should().BeTrue("because the forced value takes priority over the feature's own rules and default");
    }

    [Fact]
    public void ForcedFeatureAppliesEvenWhenTheFeatureIsNotDefined()
    {
        const string FeatureName = "not-a-real-feature";

        var context = new Context
        {
            Features = new Dictionary<string, Feature>(),
            ForcedFeatures = new Dictionary<string, JToken> { [FeatureName] = "forced-value" }
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Source.Should().Be(FeatureResult.SourceId.Override, "because forced overrides apply regardless of whether the feature exists");
        result.Value?.ToString().Should().Be("forced-value");
    }

    [Fact]
    public void ForcedFeatureCanForceAJsonNullValue()
    {
        const string FeatureName = "nullable-feature";

        var context = new Context
        {
            Features = new Dictionary<string, Feature> { [FeatureName] = new Feature { DefaultValue = true } },
            ForcedFeatures = new Dictionary<string, JToken> { [FeatureName] = null }
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);

        result.Source.Should().Be(FeatureResult.SourceId.Override);
        result.Value.Type.Should().Be(JTokenType.Null, "because the forced value was explicitly null");
        result.On.Should().BeFalse("because a null value is falsy");
    }

    [Fact]
    public void SetForcedFeaturesUpdatesOverridesAtRuntime()
    {
        const string FeatureName = "runtime-feature";

        var context = new Context
        {
            Features = new Dictionary<string, Feature> { [FeatureName] = new Feature { DefaultValue = false } }
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(FeatureName).Source.Should().Be(FeatureResult.SourceId.DefaultValue, "because nothing is forced yet");

        growthBook.SetForcedFeatures(new Dictionary<string, JToken> { [FeatureName] = true });

        var forcedResult = growthBook.EvalFeature(FeatureName);
        forcedResult.Source.Should().Be(FeatureResult.SourceId.Override);
        forcedResult.On.Should().BeTrue();

        growthBook.SetForcedFeatures(null);

        growthBook.EvalFeature(FeatureName).Source.Should().Be(FeatureResult.SourceId.DefaultValue, "because clearing the overrides should restore normal evaluation");
    }

    [Fact]
    public void SetForcedFeaturesReplacesContextLevelOverridesEntirely()
    {
        const string ContextFeature = "context-level-feature";
        const string RuntimeFeature = "runtime-level-feature";

        var context = new Context
        {
            Features = new Dictionary<string, Feature>
            {
                [ContextFeature] = new Feature { DefaultValue = false },
                [RuntimeFeature] = new Feature { DefaultValue = false }
            },
            ForcedFeatures = new Dictionary<string, JToken> { [ContextFeature] = true }
        };

        var growthBook = new GrowthBook(context);

        // SetForcedFeatures fully replaces whatever was configured on the Context - it does not merge.
        // This mirrors the reference SDK's setForcedFeatures, which assigns over a single
        // forcedFeatureValues slot rather than layering on top of it.
        growthBook.SetForcedFeatures(new Dictionary<string, JToken> { [RuntimeFeature] = true });

        growthBook.EvalFeature(ContextFeature).Source.Should().Be(FeatureResult.SourceId.DefaultValue, "because SetForcedFeatures replaced the context-level override, it wasn't merged with it");
        growthBook.EvalFeature(RuntimeFeature).On.Should().BeTrue("because the runtime override was just set");
    }

    [Fact]
    public void ContextCloneCreatesAnIndependentCopyOfForcedFeatures()
    {
        var context = new Context
        {
            ForcedFeatures = new Dictionary<string, JToken> { ["shared-feature"] = true }
        };

        var cloned = context.Clone();
        cloned.ForcedFeatures["shared-feature"] = false;
        cloned.ForcedFeatures["clone-only-feature"] = true;

        context.ForcedFeatures["shared-feature"].Value<bool>().Should().BeTrue("because mutating the clone must not affect the original context");
        context.ForcedFeatures.Should().NotContainKey("clone-only-feature");
    }
}
