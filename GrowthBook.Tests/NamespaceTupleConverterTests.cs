using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using GrowthBook.Tests.Json;

using Newtonsoft.Json;

using Xunit;

namespace GrowthBook.Tests
{
    public class NamespaceTupleConverterTests
    {
        [Fact]
        public void CreateFromJson_NoFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("GrowthBookContext.NoFeatures");

            var gb = JsonConvert.DeserializeObject<Context>(json);
        }

        [Fact]
        public void CreateFromJson_WithFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("GrowthBookContext");

            var gb = JsonConvert.DeserializeObject<Context>(json);
        }

        [Fact]
        public void CreateFeaturesFromJson_WithFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("FeatureDictionary");

            var gb = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
        }

        [Fact]
        public void CreateFeaturesFromJson_OneFeatureWithNameSpace_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("SingleFeatureDictionary.WithNameSpace");

            var gb = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
        }


    }

}
