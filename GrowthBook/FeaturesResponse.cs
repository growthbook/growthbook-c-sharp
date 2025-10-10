using System.Collections.Generic;

namespace GrowthBook
{
    internal sealed class FeaturesResponse
    {
        public int FeatureCount => Features?.Count ?? 0;
        public Dictionary<string, Feature>? Features { get; set; }
        public string? EncryptedFeatures { get; set; }
    }
}
