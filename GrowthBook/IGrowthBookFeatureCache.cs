using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;

namespace GrowthBook
{
    /// <summary>
    /// Represents a cache for GrowthBook features. The default GrowthBook implementation uses <see cref="InMemoryFeatureCache"/>
    /// but it could be replaced by a Redis-backed cache or similar in a more distributed scenario.
    /// </summary>
    public interface IGrowthBookFeatureCache
    {
        /// <summary>
        /// The count of distinct features currently in the cache.
        /// </summary>
        int FeatureCount { get; }

        /// <summary>
        /// Gets a value indicating whether the cache has expired.
        /// </summary>
        bool IsCacheExpired { get; }

        /// <summary>
        /// Get all features from the cache.
        /// </summary>
        /// <param name="cancellationToken">Used to monitor whether the retrieval action should be cancelled.</param>
        /// <returns>A <see cref="Task"/> that represents the retrieval action.</returns>
        Task<IDictionary<string, Feature>> GetFeatures(CancellationToken? cancellationToken = null);

        /// <summary>
        /// Refreshes the cache with the provided features. This will replace all features currently in the cache
        /// and should ideally happen when the cache has either not been populated yet or has expired.
        /// </summary>
        /// <param name="features">The features to cache.</param>
        /// <param name="cancellationToken">Used to monitor whether the cache action should be cancelled.</param>
        /// <returns>A <see cref="Task"/> that represents the refresh action.</returns>
        Task RefreshWith(IDictionary<string, Feature>? features, CancellationToken? cancellationToken = null);
    }
}
