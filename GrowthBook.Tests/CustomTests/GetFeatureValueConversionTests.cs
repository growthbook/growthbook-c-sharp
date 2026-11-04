using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers what <see cref="GrowthBook.GetFeatureValue{T}"/> does when the feature's value cannot be read
/// as the requested type. The reference SDK returns <c>value === null ? defaultValue : value</c> and never
/// converts, so this failure mode is specific to a typed SDK - and the fallback parameter exists precisely
/// so the caller has something to fall back to.
/// </summary>
public class GetFeatureValueConversionTests
{
    /// <summary>
    /// Serves a fixed feature set. Needed because the async accessors await <c>LoadFeatures</c> on every
    /// call, which would otherwise reach for the real API.
    /// </summary>
    private sealed class StubRepository : IGrowthBookFeatureRepository
    {
        private readonly IDictionary<string, Feature> _features;
        private readonly HashSet<string> _tracked = new HashSet<string>();

        public StubRepository(IDictionary<string, Feature> features) => _features = features;

        public Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, System.Threading.CancellationToken? cancellationToken = null)
            => Task.FromResult(_features);

        public Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, System.Threading.CancellationToken? cancellationToken = null)
            => Task.FromResult(_features);

        public void Cancel() { }

        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment) => false;

        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment) { }

        public bool IsAlreadyTracked(string trackingKey) => _tracked.Contains(trackingKey);

        public void MarkAsTracked(string trackingKey) => _tracked.Add(trackingKey);

        public bool TryMarkAsTracked(string trackingKey) => _tracked.Add(trackingKey);
    }

    private static GrowthBook WithValue(JToken defaultValue)
    {
        var features = new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = defaultValue } };

        return new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = features,
            FeatureRepository = new StubRepository(features)
        });
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("yes")]
    public void AStringValueReadAsBoolReturnsTheFallbackInsteadOfThrowing(string featureValue)
    {
        var growthBook = WithValue(new JValue(featureValue));

        growthBook.Invoking(g => g.GetFeatureValue("flag", false))
            .Should().NotThrow("because a value that cannot be converted is exactly what the fallback is for");

        growthBook.GetFeatureValue("flag", false).Should().BeFalse();
        growthBook.GetFeatureValue("flag", true).Should().BeTrue("because the caller's fallback is returned verbatim, not a coerced guess");
    }

    [Fact]
    public void AnObjectValueReadAsBoolReturnsTheFallback()
    {
        var growthBook = WithValue(new JObject());

        growthBook.GetFeatureValue("flag", true).Should().BeTrue();
    }

    [Fact]
    public void AStringValueReadAsIntReturnsTheFallback()
    {
        var growthBook = WithValue(new JValue("not-a-number"));

        growthBook.GetFeatureValue("flag", 42).Should().Be(42);
    }

    [Fact]
    public void ConvertibleValuesAreStillConverted()
    {
        WithValue(new JValue(0)).GetFeatureValue("flag", true).Should().BeFalse("because 0 converts to false");
        WithValue(new JValue("false")).GetFeatureValue("flag", true).Should().BeFalse();
        WithValue(new JValue(true)).GetFeatureValue("flag", false).Should().BeTrue();
        WithValue(new JValue(7)).GetFeatureValue("flag", 0).Should().Be(7);
        WithValue(new JValue("hello")).GetFeatureValue("flag", "x").Should().Be("hello");
        WithValue(new JValue(1.5)).GetFeatureValue("flag", 0d).Should().Be(1.5);
    }

    [Fact]
    public void AJsonNullValueStillReturnsTheFallback()
    {
        var growthBook = WithValue(JValue.CreateNull());

        growthBook.GetFeatureValue("flag", "fallback").Should().Be("fallback",
            "because a JValue wrapping JSON null is not a usable value even though it is not a CLR null");
    }

    [Fact]
    public void AnUnknownFeatureStillReturnsTheFallback()
    {
        var growthBook = WithValue(new JValue(true));

        growthBook.GetFeatureValue("no-such-flag", "fallback").Should().Be("fallback");
    }

    [Fact]
    public void ComplexTypesStillDeserialize()
    {
        var growthBook = WithValue(JObject.FromObject(new { name = "config", size = 3 }));

        var value = growthBook.GetFeatureValue<JObject>("flag", null);

        value.Should().NotBeNull();
        value["name"].Value<string>().Should().Be("config");
        value["size"].Value<int>().Should().Be(3);
    }

    [Fact]
    public async Task TheAsyncOverloadBehavesTheSameWay()
    {
        var growthBook = WithValue(new JValue("off"));

        var value = await growthBook.GetFeatureValueAsync("flag", false);

        value.Should().BeFalse("because both overloads have to agree - they share the same conversion path");
    }

    [Fact]
    public void TheFallbackIsNotReturnedForValuesThatMerelyLookFalsy()
    {
        WithValue(new JValue("off")).GetFeatureValue("flag", "fallback")
            .Should().Be("off", "because reading it as a string needs no conversion and must not hit the fallback");
    }
}
 