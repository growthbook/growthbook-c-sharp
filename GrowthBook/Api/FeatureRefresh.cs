using System.Collections.Generic;

namespace GrowthBook.Api
{
    /// <summary>
    /// A set of refreshed features together with the position of the refresh that produced them.
    /// Versions rise with every refresh from the same source, so a subscriber can tell a refresh that
    /// overtook another on the way to it from one that is genuinely newer.
    /// </summary>
    public sealed class FeatureRefresh
    {
        public FeatureRefresh(long version, IDictionary<string, Feature> features)
        {
            Version = version;
            Features = features;
        }

        /// <summary>
        /// The position of this refresh in its source's sequence, starting at one.
        /// </summary>
        public long Version { get; }

        /// <summary>
        /// The features as of this refresh.
        /// </summary>
        public IDictionary<string, Feature> Features { get; }
    }
}
