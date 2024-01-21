using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Represents a background worker that is responsible for refreshing the cache from the GrowthBook API on demand.
    /// </summary>
    public interface IGrowthBookFeatureRefreshWorker
    {
        /// <summary>
        /// Cancels any active refresh listeners and operations.
        /// </summary>
        void Cancel();

        /// <summary>
        /// Retrieves the latest features from the GrowthBook API and caches them.
        /// </summary>
        /// <param name="cancellationToken">Used to monitor whether the retrieval and cache actions should be cancelled.</param>
        /// <returns>A <see cref="Task{Task{IDictionary{string, Feature}}}"/> that represents the retrieval and cache actions.</returns>
        Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null);
    }
}
