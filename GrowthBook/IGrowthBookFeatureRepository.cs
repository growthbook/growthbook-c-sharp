using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Represents a repository for GrowthBook features loaded from the Features API.
    /// </summary>
    public interface IGrowthBookFeatureRepository
    {
        /// <summary>
        /// Cancels any ongoing operations, such as background listeners.
        /// </summary>
        void Cancel();

        /// <summary>
        /// Gets all of the features in the repository, taking any provided options into account.
        /// </summary>
        /// <param name="options">A set of options to determine how the features are retrieved. Optional.</param>
        /// <param name="cancellationToken">Used for monitoring the need to cancel a feature retrieval.</param>
        /// <returns>A <see cref="Task{IDictionary{string, Feature}}"/> that represents the retrieval action.</returns>
        Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Checks if the experiment assignment has already been recorded to prevent duplicate callbacks.
        /// </summary>
        /// <param name="experimentKey">The experiment key</param>
        /// <param name="assignment">The experiment assignment</param>
        /// <returns>True if the assignment already exists and is identical</returns>
        bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment);

        /// <summary>
        /// Records an experiment assignment to prevent duplicate subscription callbacks.
        /// </summary>
        /// <param name="experimentKey">The experiment key</param>
        /// <param name="assignment">The experiment assignment to record</param>
        void RecordAssignment(string experimentKey, ExperimentAssignment assignment);

        /// <summary>
        /// Checks if a tracking callback has already been sent for this experiment combination.
        /// </summary>
        /// <param name="trackingKey">The tracking key (combination of hash attribute, hash value, experiment key, and variation ID)</param>
        /// <returns>True if already tracked</returns>
        bool IsAlreadyTracked(string trackingKey);

        /// <summary>
        /// Marks a tracking callback as sent to prevent duplicate tracking calls.
        /// </summary>
        /// <param name="trackingKey">The tracking key to mark as tracked</param>
        void MarkAsTracked(string trackingKey);

        /// <summary>
        /// Atomically tries to mark a tracking callback as sent. Returns true if successfully marked (wasn't tracked before).
        /// This prevents race conditions in concurrent scenarios.
        /// </summary>
        /// <param name="trackingKey">The tracking key to mark as tracked</param>
        /// <returns>True if successfully marked as tracked (wasn't tracked before), false if already tracked</returns>
        bool TryMarkAsTracked(string trackingKey);

        /// <summary>
        /// Gets features using remote evaluation if enabled in the context, otherwise falls back to regular feature retrieval.
        /// </summary>
        /// <param name="context">The GrowthBook context containing remote evaluation settings and user attributes</param>
        /// <param name="options">A set of options to determine how the features are retrieved. Optional.</param>
        /// <param name="cancellationToken">Used for monitoring the need to cancel a feature retrieval.</param>
        /// <returns>A <see cref="Task{IDictionary{string, Feature}}"/> that represents the retrieval action.</returns>
        Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);
    }
}
