using System.Collections.Generic;
using GrowthBook.Tests.Json;
using Newtonsoft.Json;
using Xunit;

namespace GrowthBook.Tests;

public class NamespaceTupleConverterTests
{
    [Fact]
    public void CreateFromJson_NoFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("GrowthBookContext.NoFeatures");
        _ = JsonConvert.DeserializeObject<Context>(json);
    }

    [Fact]
    public void CreateFromJson_WithFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("GrowthBookContext");
        _ = JsonConvert.DeserializeObject<Context>(json);
    }

    [Fact]
    public void CreateFeaturesFromJson_WithFeatures_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("FeatureDictionary");
        _ = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
    }

    [Fact]
    public void CreateFeaturesFromJson_OneFeatureWithNameSpace_ShouldSucceed()
    {
        string json = JsonTestHelpers.GetTestJson("SingleFeatureDictionary.WithNameSpace");
        _ = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
    }
}
