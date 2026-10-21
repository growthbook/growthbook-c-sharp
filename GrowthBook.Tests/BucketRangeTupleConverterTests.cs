using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests;

public class BucketRangeTupleConverterTests
{

    [Fact]
    public void ReadJson_ValidArray_ShouldDeserializeCorrectly()
    {
        var json = "[0.0, 0.5]";
        var result = JsonConvert.DeserializeObject<BucketRange>(json);

        Assert.NotNull(result);
        Assert.Equal(0.0, result.Start);
        Assert.Equal(0.5, result.End);
    }

    [Fact]
    public void ReadJson_FullPrecisionValues_ShouldBeExact()
    {
        var json = "[0.3333333333333333, 0.6666666666666666]";
        var result = JsonConvert.DeserializeObject<BucketRange>(json);

        Assert.NotNull(result);
        Assert.Equal(1.0 / 3, result.Start);
        Assert.Equal(2.0 / 3, result.End);
    }

    [Fact]
    public void ReadJson_IntegerValues_ShouldDeserialize()
    {
        var result = JsonConvert.DeserializeObject<BucketRange>("[0, 1]");

        Assert.NotNull(result);
        Assert.Equal(0.0, result.Start);
        Assert.Equal(1.0, result.End);
    }

    [Theory]
    [InlineData("[\"abc\", \"def\"]")]
    [InlineData("[{}, 0.5]")]
    [InlineData("[true, false]")]
    [InlineData("[\"0\", \"0.5\"]")]
    [InlineData("[null, 0.5]")]
    [InlineData("[0.5, null]")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[0.5]")]
    [InlineData("[0, 0.5, 1]")]
    [InlineData("{}")]
    public void ReadJson_MalformedRange_ShouldReturnNull(string json)
    {
        BucketRange result = null;
        var ex = Record.Exception(() => result = JsonConvert.DeserializeObject<BucketRange>(json));

        Assert.Null(ex);
        Assert.Null(result);
    }

    [Fact]
    public void ReadJson_MalformedRangeInsideFeature_ShouldNotThrow()
    {
        var json = @"{
              ""defaultValue"": null,
              ""rules"": [{
                ""ranges"": [[""abc"", ""def""], [0.5, 1.0]]
              }]
            }";

        var feature = JsonConvert.DeserializeObject<Feature>(json);

        Assert.Null(feature.Rules[0].Ranges[0]);
        Assert.Equal(0.5, feature.Rules[0].Ranges[1].Start);
        Assert.Equal(1.0, feature.Rules[0].Ranges[1].End);
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

        var feature = JsonConvert.DeserializeObject<Feature>(json);

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
        var json = JsonConvert.SerializeObject(bucketRange);
        var array = JArray.Parse(json);

        Assert.Equal(2, array.Count);
        Assert.Equal(0.0, array[0].Value<double>());
        Assert.Equal(0.5, array[1].Value<double>());
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

        var ex = Record.Exception(() => JsonConvert.SerializeObject(features));
        Assert.Null(ex);
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_ShouldPreserveValues()
    {
        var original = new BucketRange(0.25, 0.75);

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<BucketRange>(json);

        Assert.Equal(original.Start, restored.Start);
        Assert.Equal(original.End, restored.End);
    }
}
