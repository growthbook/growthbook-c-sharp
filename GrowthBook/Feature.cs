using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;

namespace GrowthBook {
    /// <summary>
    /// Represents an object consisting of a default value plus rules that can override the default.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class Feature {
        /// <summary>
        /// The default value (should use null if not specified)
        /// </summary>
        public JToken DefaultValue { get; set; }

        /// <summary>
        /// Array of FeatureRule objects that determine when and how the defaultValue gets overridden.
        /// </summary>
        public IList<FeatureRule> Rules { get; set; } = new List<FeatureRule>();

        /// <summary>
        /// Returns the default value of the feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The default value of the feature cast to the specified type.</returns>
        public T GetDefaultValue<T>() {
            return DefaultValue.ToObject<T>();
        }
    }
}