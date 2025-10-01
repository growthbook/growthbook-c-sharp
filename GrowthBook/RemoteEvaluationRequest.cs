using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents a request payload for remote evaluation API calls.
    /// This model matches the expected format for POST /api/eval/:clientKey endpoint.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class RemoteEvaluationRequest
    {
        /// <summary>
        /// User attributes used for feature evaluation and experiment assignment.
        /// </summary>
        [JsonProperty("attributes")]
        public JObject Attributes { get; set; } = new JObject();

        /// <summary>
        /// Map of experiment keys to forced variation indices.
        /// Used for QA testing and debugging.
        /// </summary>
        [JsonProperty("forcedVariations")]
        public IDictionary<string, int> ForcedVariations { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// Array of [key, value] pairs for forced features.
        /// Used for testing and overriding feature values.
        /// Format: [["featureKey", value], ["anotherKey", value2]]
        /// </summary>
        [JsonProperty("forcedFeatures")]
        public List<List<object>> ForcedFeatures { get; set; } = new List<List<object>>();

        /// <summary>
        /// The current URL for URL-based targeting rules.
        /// Optional parameter used for experiments with URL conditions.
        /// </summary>
        [JsonProperty("url")]
        public string? Url { get; set; }

        /// <summary>
        /// Creates a new RemoteEvaluationRequest with default values.
        /// </summary>
        public RemoteEvaluationRequest()
        {
        }

        /// <summary>
        /// Creates a new RemoteEvaluationRequest from a GrowthBook Context.
        /// </summary>
        /// <param name="context">The GrowthBook context to extract data from</param>
       /// <summary>
        /// Creates a new RemoteEvaluationRequest from a GrowthBook Context.
        /// </summary>
        /// <param name="context">The GrowthBook context to extract data from</param>
        public static RemoteEvaluationRequest FromContext(Context context)
        {
            if (context == null)
                return new RemoteEvaluationRequest();

            // Convert ForcedFeatures dictionary to list of [key, value] pairs
            var forcedFeaturesList = new List<List<object>>();
            if (context.ForcedFeatures != null)
            {
                foreach (var entry in context.ForcedFeatures)
                {
                    forcedFeaturesList.Add(new List<object> { entry.Key, entry.Value });
                }
            }

            return new RemoteEvaluationRequest
            {
                Attributes = context.Attributes?.DeepClone() as JObject ?? new JObject(),
                ForcedVariations = new Dictionary<string, int>(context.ForcedVariations ?? new Dictionary<string, int>()),
                ForcedFeatures = forcedFeaturesList,
                Url = context.Url ?? ""
            };
        }

    }
}
