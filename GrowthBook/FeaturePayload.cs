using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// The features from one payload together with the contextual bandit definitions they were published with.
    /// </summary>
    /// <remarks>
    /// The two parts have to be adopted together. A rule names a bandit and supplies its own variation list, so
    /// pairing this payload's rules with the previous payload's weights can bucket a user against a variation
    /// list that no longer exists. Publishing them as two references, however carefully each is written, leaves a
    /// window where a reader sees one new and one old; one reference read once has no such window.
    /// </remarks>
    public sealed class FeaturePayload
    {
        public FeaturePayload(IDictionary<string, Feature> features, IDictionary<string, ContextualBanditDefinition> contextualBandits)
        {
            Features = features;
            ContextualBandits = contextualBandits;
        }

        /// <summary>
        /// The features the payload carried.
        /// </summary>
        public IDictionary<string, Feature> Features { get; }

        /// <summary>
        /// The bandit definitions the payload carried, keyed by the reference a feature rule points at, or null
        /// when it carried none.
        /// </summary>
        public IDictionary<string, ContextualBanditDefinition> ContextualBandits { get; }
    }
}
