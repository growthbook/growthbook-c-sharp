using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class SetFeatureUsageCallbackTests : UnitTest
{
    private static Context CreateContext(string featureName) => new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature>
        {
            [featureName] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule>
                {
                    new FeatureRule { Variations = new JArray(false, true), Coverage = 1d }
                }
            }
        }
    };

    [Fact]
    public void SetFeatureUsageCallbackReplacesTheCallbackFromContext()
    {
        const string FeatureName = "test-feature";

        var fromContext = new List<string>();
        var fromSetter = new List<string>();

        var context = CreateContext(FeatureName);
        context.OnFeatureUsage = (key, _) => fromContext.Add(key);

        var growthBook = new GrowthBook(context);
        growthBook.EvalFeature(FeatureName);

        fromContext.Should().ContainSingle("because the context callback was in effect");

        growthBook.SetFeatureUsageCallback((key, _) => fromSetter.Add(key));
        growthBook.EvalFeature(FeatureName);

        fromSetter.Should().ContainSingle("because the replacement callback should now receive usage");
        fromContext.Should().ContainSingle("because the original callback must no longer be called");
    }

    [Fact]
    public void SetFeatureUsageCallbackWithNullStopsReporting()
    {
        const string FeatureName = "test-feature";

        var usages = new List<string>();

        var context = CreateContext(FeatureName);
        context.OnFeatureUsage = (key, _) => usages.Add(key);

        var growthBook = new GrowthBook(context);
        growthBook.EvalFeature(FeatureName);
        usages.Should().ContainSingle();

        growthBook.SetFeatureUsageCallback(null);
        growthBook.EvalFeature(FeatureName);

        usages.Should().ContainSingle("because clearing the callback must stop further reporting");
    }

    [Fact]
    public void ReplacingTheCallbackResetsTheDedupCacheSoTheNewCallbackSeesCurrentState()
    {
        const string FeatureName = "test-feature";

        var usages = new List<string>();

        var context = CreateContext(FeatureName);
        context.OnFeatureUsage = (_, __) => { };

        var growthBook = new GrowthBook(context);

        // The original callback consumes the first (deduped) usage for this key.
        growthBook.EvalFeature(FeatureName);

        growthBook.SetFeatureUsageCallback((key, _) => usages.Add(key));
        growthBook.EvalFeature(FeatureName);

        usages.Should().ContainSingle("because dedup is keyed per feature, so a replacement callback would otherwise never hear about features already seen");
    }

    [Fact]
    public void SetFeatureUsageCallbackIsReachableThroughTheInterface()
    {
        const string FeatureName = "test-feature";

        var usages = new List<string>();

        // Consumers that inject IGrowthBook via DI need this on the interface, not just the concrete class.
        IGrowthBook growthBook = new GrowthBook(CreateContext(FeatureName));

        growthBook.SetFeatureUsageCallback((key, _) => usages.Add(key));
        growthBook.EvalFeature(FeatureName);

        usages.Should().ContainSingle();
    }
}
