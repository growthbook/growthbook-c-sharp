using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    /// <summary>
    /// Represents a set of options that can affect how a
    /// particular <see cref="IGrowthBookFeatureRepository"/> implementation
    /// handles requests to the GrowthBook API.
    /// </summary>
    public class GrowthBookRetrievalOptions
    {
        /// <summary>
        /// Indicates that any cached features should by ignored and that the cache should be refreshed from the API.
        /// </summary>
        public bool ForceRefresh { get; set; }

        /// <summary>
        /// Whether the caller prefers to wait for the API call to fully complete prior to retrieving
        /// features from the cache or whether the cache should be immediately used in the interim.
        /// </summary>
        public bool WaitForCompletion { get; set; }
    }
}
