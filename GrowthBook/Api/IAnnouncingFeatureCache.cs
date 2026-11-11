using System.Collections.Generic;

namespace GrowthBook.Api
{
    /// <summary>
    /// A cache that can store a refresh and announce it to its subscribers as two steps. The refresh worker writes
    /// under its write lock and announces once the lock is released, so a subscriber that refreshes again from its
    /// handler cannot wait on a lock its own caller holds.
    /// </summary>
    internal interface IAnnouncingFeatureCache
    {
        /// <summary>Stores the features without telling the subscribers, and returns the refresh to announce.</summary>
        FeatureRefresh StoreWithoutAnnouncing(IDictionary<string, Feature> features);

        /// <summary>Tells the subscribers about a refresh stored earlier.</summary>
        void Announce(FeatureRefresh refresh);
    }
}
