using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    /// <summary>
    /// Represents meta info about an experiment variation.
    /// </summary>
    public class VariationMeta
    {
        /// <summary>
        ///  A unique key for this variation. Optional.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// A human-readable name for this variation. Optional.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Used to implement holdout groups. Optional.
        /// </summary>
        public bool Passthrough { get; set; }
    }
}
