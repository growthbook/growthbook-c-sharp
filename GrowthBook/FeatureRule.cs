using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;

namespace GrowthBook {
    /// <summary>
    /// Overrides the defaultValue of a Feature.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class FeatureRule {
        /// <summary>
        /// Optional targeting condition.
        /// </summary>
        public JObject Condition { get; set; }

        /// <summary>
        /// What percent of users should be included in the experiment (between 0 and 1, inclusive).
        /// </summary>
        public double Coverage { get; set; } = 1;

        /// <summary>
        /// Immediately force a specific value (ignore every other option besides condition and coverage).
        /// </summary>
        public JToken Force { get; set; }

        /// <summary>
        /// Run an experiment (A/B test) and randomly choose between these variations.
        /// </summary>
        public JArray Variations { get; set; }

        /// <summary>
        /// The globally unique tracking key for the experiment (default to the feature key).
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// How to weight traffic between variations. Must add to 1.
        /// </summary>
        public IList<double> Weights { get; set; }

        /// <summary>
        /// Adds the experiment to a namespace.
        /// </summary>
        public Namespace Namespace { get; set; }

        /// <summary>
        /// What user attribute should be used to assign variations (defaults to id).
        /// </summary>
        public string HashAttribute { get; set; } = "id";

        /// <summary>
        /// Returns the feature variations cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The variations cast as the specified type.</returns>
        public T GetVariations<T>() {
            return Variations.ToObject<T>();
        }
    }
}