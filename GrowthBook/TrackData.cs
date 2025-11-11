using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    /// <summary>
    /// Represents the track data associated with a feature rule.
    /// </summary>
    public class TrackData
    {
        /// <summary>
        /// The tracked experiment.
        /// </summary>
        public Experiment? Experiment { get; set; }

        /// <summary>
        /// The tracked experiment result.
        /// </summary>
        public ExperimentResult? Result { get; set; }
    }
}
