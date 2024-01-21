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
    }
}
