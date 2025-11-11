using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    /// <summary>
    /// Represents an object used for mutual exclusion and filtering users out of experiments based on random hashes.
    /// </summary>
    public class Filter
    {
        /// <summary>
        /// The seed used in the hash.
        /// </summary>
        public string? Seed { get; set; }

        /// <summary>
        /// Array of ranges that are included.
        /// </summary>
        public BucketRange[]? Ranges { get; set; }

        /// <summary>
        /// The hash version to use (defaults to 2).
        /// </summary>
        public int HashVersion { get; set; } = 2;

        /// <summary>
        /// The attribute to use (defaults to "id").
        /// </summary>
        public string Attribute { get; set; } = "id";
    }
}
