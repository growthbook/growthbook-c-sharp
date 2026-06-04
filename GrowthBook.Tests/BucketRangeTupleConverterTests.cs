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
