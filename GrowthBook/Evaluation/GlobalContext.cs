using System;
using System.Collections.Generic;
using GrowthBook.Services;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Evaluation
{
    /// <summary>Shared singleton state for an evaluation: features, global config, and callbacks.</summary>
    internal sealed class GlobalContext
    {
        public IDictionary<string, Feature> Features { get; set; } = new Dictionary<string, Feature>();
        public JObject SavedGroups { get; set; }
        public IList<Experiment> Experiments { get; set; } = new List<Experiment>();
        public bool Enabled { get; set; } = true;
        public bool QaMode { get; set; }
        public IDictionary<string, int> ForcedVariations { get; set; } = new Dictionary<string, int>();
        public JObject Attributes { get; set; }
        public Action<Experiment, ExperimentResult> TrackingCallback { get; set; }
        /// <summary>Records the assignment and notifies subscribers if it changed.</summary>
        internal Action<Experiment, ExperimentResult> OnExperimentAssigned { get; set; }

        /// <summary>Notifies subscribers without recording an assignment, for a forced value.</summary>
        internal Action<Experiment, ExperimentResult> OnSubscriberNotification { get; set; }
        public IStickyBucketService StickyBucketService { get; set; }
    }
}
