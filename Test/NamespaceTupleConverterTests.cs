using System.Collections.Generic;
using GrowthBook;
using Growthbook.Tests.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Test
{
    [TestClass]
    public class NamespaceTupleConverterTests
    {
        [TestMethod]
        public void CreateFromJson_NoFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("GrowthBookContext.NoFeatures");

            var gb = JsonConvert.DeserializeObject<Context>(json);
        }

        [TestMethod]
        public void CreateFromJson_WithFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("GrowthBookContext");

            var gb = JsonConvert.DeserializeObject<Context>(json);
        }

        [TestMethod]
        public void CreateFeaturesFromJson_WithFeatures_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("FeatureDictionary");

            var gb = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
        }

        [TestMethod]
        public void CreateFeaturesFromJson_OneFeatureWithNameSpace_ShouldSucceed()
        {
            string json = JsonTestHelpers.GetTestJson("SingleFeatureDictionary.WithNameSpace.");

            var gb = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(json);
        }

    }
}
