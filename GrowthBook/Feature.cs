
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace GrowthBook
{
    /// <summary>
    /// Represents an object consisting of a default value plus rules that can override the default.
    /// </summary>
    public class Feature
    {
        /// <summary>
        /// The default value (should use null if not specified)
        /// </summary>
        public JsonNode? DefaultValue { get; set; }

        /// <summary>
        /// Array of FeatureRule objects that determine when and how the defaultValue gets overridden.
        /// </summary>
        public IList<FeatureRule>? Rules { get; set; }

        /// <summary>
        /// Returns the default value of the feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The default value of the feature cast to the specified type.</returns>
        public T? GetDefaultValue<T>()
        {
            if (DefaultValue == null)
                return default;

            var typeInfo = GrowthBookJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfo == null)
                return default;

            return (T?)DefaultValue.Deserialize((JsonTypeInfo<T>)typeInfo);
        }
    }
}
