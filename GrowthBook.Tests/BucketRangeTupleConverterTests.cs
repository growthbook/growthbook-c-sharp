using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace GrowthBook.Tests;

public class BucketRangeConverterTests
{
    [Fact]
    public void ReadJson_ValidArray_ShouldDeserializeCorrectly()
    {
        var json = "[0.0, 0.5]";
        var result = JsonSerializer.Deserialize<BucketRange>(json, GrowthBookJsonContext.Default.BucketRange);

        Assert.NotNull(result);
        Assert.Equal(0.0, result.Start);
        Assert.Equal(0.5, result.End);
    }

    [Fact]
    public void ReadJson_InsideFeature_ShouldDeserializeCorrectly()
    {
        var json = @"{
              ""defaultValue"": null,
              ""rules"": [{
                ""ranges"": [[0.0, 0.5], [0.5, 1.0]]
              }]
            }";

        var feature = JsonSerializer.Deserialize<Feature>(json, GrowthBookJsonContext.Default.Feature);

        Assert.NotNull(feature);
        Assert.Equal(0.0, feature.Rules[0].Ranges[0].Start);
        Assert.Equal(0.5, feature.Rules[0].Ranges[0].End);
        Assert.Equal(0.5, feature.Rules[0].Ranges[1].Start);
        Assert.Equal(1.0, feature.Rules[0].Ranges[1].End);
    }

    [Fact]
    public void WriteJson_ShouldSerializeAsTwoElementArray()
    {
        var bucketRange = new BucketRange(0.0, 0.5);
        var json = JsonSerializer.Serialize(bucketRange, GrowthBookJsonContext.Default.BucketRange);
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement;

        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal(0.0, array[0].GetDouble());
        Assert.Equal(0.5, array[1].GetDouble());
    }

    [Fact]
    public void WriteJson_InsideFeatureDictionary_ShouldNotThrow()
    {
        var features = new Dictionary<string, Feature>
        {
            ["test-feature"] = new Feature
            {
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Ranges = new[] { new BucketRange(0.0, 0.5), new BucketRange(0.5, 1.0) }
                    }
                }
            }
        };

        var ex = Record.Exception(() => JsonSerializer.Serialize(features, GrowthBookJsonContext.Default.DictionaryStringFeature));
        Assert.Null(ex);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_ShouldPreserveValues()
    {
        var original = new BucketRange(0.25, 0.75);

        var json = JsonSerializer.Serialize(original, GrowthBookJsonContext.Default.BucketRange);
        var restored = JsonSerializer.Deserialize<BucketRange>(json, GrowthBookJsonContext.Default.BucketRange);

        Assert.Equal(original.Start, restored.Start);
        Assert.Equal(original.End, restored.End);
    }
}
