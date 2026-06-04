using System.Collections.Generic;
using GrowthBook.Tests.Json;
using System.Text.Json;
using Xunit;

namespace GrowthBook.Tests;

public class NamespaceTupleConverterTests
{
    [Fact]
    public void CreateFromJson_NoFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("GrowthBookContext.NoFeatures");
        _ = JsonSerializer.Deserialize(json, GrowthBookJsonContext.Default.Context);
    }

    [Fact]
    public void CreateFromJson_WithFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("GrowthBookContext");
        _ = JsonSerializer.Deserialize(json, GrowthBookJsonContext.Default.Context);
    }

    [Fact]
    public void CreateFeaturesFromJson_WithFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("FeatureDictionary");
        _ = JsonSerializer.Deserialize(json, GrowthBookJsonContext.Default.DictionaryStringFeature);
    }

    [Fact]
    public void CreateFeaturesFromJson_OneFeatureWithNameSpace_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("SingleFeatureDictionary.WithNameSpace");
        _ = JsonSerializer.Deserialize(json, GrowthBookJsonContext.Default.DictionaryStringFeature);
    }
}
