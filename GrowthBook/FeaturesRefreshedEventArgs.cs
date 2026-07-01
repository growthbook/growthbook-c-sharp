using System;
using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// Data for the <see cref="IGrowthBook.FeaturesRefreshed"/> event, describing a feature
    /// refresh confirmed by the server (HTTP 200, HTTP 304 Not Modified, or a server-sent event).
    /// </summary>
    public class FeaturesRefreshedEventArgs : EventArgs
    {
        /// <summary>
        /// True when the server returned changed features (HTTP 200 or an SSE update);
        /// false when the server confirmed the cache is still valid (HTTP 304 Not Modified).
        /// </summary>
        public bool WasModified { get; }

        /// <summary>
        /// How the refresh was delivered.
        /// </summary>
        public FeatureRefreshSource Source { get; }

        /// <summary>
        /// A snapshot of the features in effect after this refresh. Never null.
        /// </summary>
        public IReadOnlyDictionary<string, Feature> Features { get; }

        /// <summary>
        /// The number of features in <see cref="Features"/>.
        /// </summary>
        public int FeatureCount { get; }

        /// <summary>
        /// The UTC time at which the refresh was observed.
        /// </summary>
        public DateTimeOffset RefreshedAt { get; }

        /// <summary>
        /// Creates a new <see cref="FeaturesRefreshedEventArgs"/>.
        /// </summary>
        /// <param name="wasModified">Whether the server returned changed features.</param>
        /// <param name="source">How the refresh was delivered.</param>
        /// <param name="features">The features in effect after the refresh.</param>
        /// <param name="refreshedAt">The UTC time the refresh was observed.</param>
        public FeaturesRefreshedEventArgs(
            bool wasModified,
            FeatureRefreshSource source,
            IReadOnlyDictionary<string, Feature> features,
            DateTimeOffset refreshedAt
        )
        {
            WasModified = wasModified;
            Source = source;
            Features = features ?? new Dictionary<string, Feature>();
            FeatureCount = Features.Count;
            RefreshedAt = refreshedAt;
        }
    }
}
