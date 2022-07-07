using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GrowthBook {
    /// <summary>
    /// Represents a mapping of an experiment to an assigned result.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ExperimentAssignment {
        /// <summary>
        /// The experiment the user is assigned to.
        /// </summary>
        public Experiment Experiment { get; set; }

        /// <summary>
        /// The experiment assignment data.
        /// </summary>
        public ExperimentResult Result { get; set; }
    }
}
