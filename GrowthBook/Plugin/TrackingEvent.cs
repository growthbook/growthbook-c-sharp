using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Represents a single analytics event sent to the GrowthBook ingestor.
    /// Use the static factory methods <see cref="ForExperiment"/> and <see cref="ForFeature"/>
    /// to create instances.
    /// </summary>
    public class TrackingEvent
    {
        /// <summary>Event name used when a user is assigned to an experiment variation.</summary>
        public const string EventExperimentViewed = "Experiment Viewed";

        /// <summary>Event name used when a feature flag is evaluated.</summary>
        public const string EventFeatureEvaluated = "Feature Evaluated";

        /// <summary>The name of the event (e.g. "Experiment Viewed").</summary>
        [JsonProperty("event_name")]
        public string EventName { get; private set; }

        /// <summary>Event-specific properties such as experiment ID or feature key.</summary>
        [JsonProperty("properties")]
        public JObject Properties { get; private set; }

        /// <summary>User attributes merged with SDK metadata (language, version).</summary>
        [JsonProperty("attributes")]
        public JObject Attributes { get; private set; }

        private TrackingEvent() {}

        /// <summary>
        /// Creates a tracking event for an experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated.</param>
        /// <param name="result">The result of the assignment.</param>
        /// <param name="attributes">User attributes to include in the event.</param>
        public static TrackingEvent ForExperiment(Experiment experiment, ExperimentResult result, JObject attributes)
        {
            return new TrackingEvent
            {
                EventName = EventExperimentViewed,
                Properties = new JObject
                {
                    ["experimentId"] = experiment?.Key, ["variationId"] = result?.VariationId,
                },
                Attributes = MergeWithSdkAttrs(attributes)
            };
        }

        /// <summary>
        /// Creates a tracking event for a feature flag evaluation.
        /// </summary>
        /// <param name="featureKey">The key of the evaluated feature.</param>
        /// <param name="result">The result of the feature evaluation.</param>
        /// <param name="attributes">User attributes to include in the event.</param>
        public static TrackingEvent ForFeature(string featureKey, FeatureResult result, JObject attributes)
        {
            var props = new JObject { ["feature"] = featureKey, ["value"] = result?.Value, ["source"] = result?.Source };

            return new TrackingEvent
            {
                EventName = EventFeatureEvaluated, Properties = props, Attributes = MergeWithSdkAttrs(attributes)
            };
        }

        private static JObject MergeWithSdkAttrs(JObject attributes)
        {
            var merged = new JObject { ["sdk_language"] = SdkMetadata.Language, ["sdk_version"] = SdkMetadata.Version };

            if (attributes != null)
            {
                merged.Merge(attributes);
            }
            return merged;
        }
    }
}
