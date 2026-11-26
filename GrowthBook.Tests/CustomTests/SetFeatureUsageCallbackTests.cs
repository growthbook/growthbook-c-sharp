using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <c>SetFeatureUsageCallback</c> replaces the callback, as <c>setFeatureUsageCallback</c> does in the JavaScript SDK. It
/// lives on <see cref="GrowthBook"/> rather than <see cref="IGrowthBook"/>, so no implementation of the interface has to
/// change.
/// </summary>
public class SetFeatureUsageCallbackTests : UnitTest
{
    private const string FeatureName = "checkout-flow";

    private static Dictionary<string, Feature> Features(bool value) => new Dictionary<string, Feature>
    {
        [FeatureName] = new Feature { DefaultValue = value }
    };

    [Fact]
    public void SetFeatureUsageCallbackReplacesTheCallbackFromContext()
    {
        var fromContext = new List<string>();
        var fromSetter = new List<string>();

        using var growthBook = new GrowthBook(new Context
        {
            Features = Features(false),
            OnFeatureUsage = (key, _) => fromContext.Add(key)
        });

        growthBook.EvalFeature(FeatureName);
        fromContext.Should().ContainSingle("the context callback was in effect");

        growthBook.SetFeatureUsageCallback((key, _) => fromSetter.Add(key));
        growthBook.Features = Features(true);
        growthBook.EvalFeature(FeatureName);

        fromSetter.Should().ContainSingle("the replacement callback receives usage now");
        fromContext.Should().ContainSingle("the original callback is no longer called");
    }

    [Fact]
    public void SetFeatureUsageCallbackWithNullStopsReporting()
    {
        var usages = new List<string>();

        using var growthBook = new GrowthBook(new Context
        {
            Features = Features(false),
            OnFeatureUsage = (key, _) => usages.Add(key)
        });

        growthBook.EvalFeature(FeatureName);
        growthBook.SetFeatureUsageCallback(null);
        growthBook.Features = Features(true);
        growthBook.EvalFeature(FeatureName);

        usages.Should().ContainSingle("clearing the callback stops further reporting");
    }

    /// <summary>
    /// As in the JavaScript SDK, replacing the callback keeps what was already reported: a value the instance has
    /// reported is not reported again to the new callback, and a changed one is.
    /// </summary>
    [Fact]
    public void ReplacingTheCallbackKeepsWhatWasAlreadyReported()
    {
        var usages = new List<FeatureResult>();

        using var growthBook = new GrowthBook(new Context
        {
            Features = Features(false),
            OnFeatureUsage = (_, _) => { }
        });

        growthBook.EvalFeature(FeatureName);
        growthBook.SetFeatureUsageCallback((_, result) => usages.Add(result));

        growthBook.EvalFeature(FeatureName);
        usages.Should().BeEmpty("the value was already reported before the callback was replaced");

        growthBook.Features = Features(true);
        growthBook.EvalFeature(FeatureName);
        usages.Should().ContainSingle().Which.On.Should().BeTrue("a changed value is new usage");
    }

    [Fact]
    public void ACallbackSetAfterConstructionIsUsedWhenNoneWasGiven()
    {
        var usages = new List<string>();

        using var growthBook = new GrowthBook(new Context { Features = Features(true) });

        growthBook.SetFeatureUsageCallback((key, _) => usages.Add(key));
        growthBook.EvalFeature(FeatureName);

        usages.Should().ContainSingle();
    }
}
