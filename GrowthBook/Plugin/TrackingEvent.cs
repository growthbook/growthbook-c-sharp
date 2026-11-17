using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Represents a single analytics event sent to the GrowthBook ingestor.
    /// Use the static factory methods <see cref="ForExperiment"/> and <see cref="ForFeature"/>
    /// to create instances.
    /// </summary>
    /// <remarks>
    /// Serialized in the shape the JavaScript SDK's tracking plugin sends (<c>getEventPayload</c>): the event's
    /// properties under <c>properties_json</c>; the identifying attributes lifted to <c>user_id</c>, <c>device_id</c>,
    /// <c>page_id</c>, <c>session_id</c>, the UTM fields and <c>page_title</c>; every other attribute under
    /// <c>context_json</c>; and the SDK's language and version at the top level.
    /// </remarks>
    public class TrackingEvent
    {
        /// <summary>Event name used when a user is assigned to an experiment variation.</summary>
        public const string EventExperimentViewed = "Experiment Viewed";

        /// <summary>Event name used when a feature flag is evaluated.</summary>
        public const string EventFeatureEvaluated = "Feature Evaluated";

        private static readonly string[] IdentifyingAttributes =
        {
            "user_id", "device_id", "anonymous_id", "id", "page_id", "session_id",
            "utmCampaign", "utmContent", "utmMedium", "utmSource", "utmTerm", "pageTitle"
        };

        /// <summary>The name of the event (e.g. "Experiment Viewed").</summary>
        [JsonProperty("event_name")]
        public string EventName { get; private set; }

        /// <summary>Event-specific properties such as experiment ID or feature key.</summary>
        [JsonProperty("properties_json")]
        public JObject Properties { get; private set; }

        [JsonProperty("user_id")]
        public string UserId { get; private set; }

        /// <summary>The <c>device_id</c> attribute, or else <c>anonymous_id</c>, or else <c>id</c>.</summary>
        [JsonProperty("device_id")]
        public string DeviceId { get; private set; }

        [JsonProperty("page_id")]
        public string PageId { get; private set; }

        [JsonProperty("session_id")]
        public string SessionId { get; private set; }

        [JsonProperty("utm_campaign", NullValueHandling = NullValueHandling.Ignore)]
        public string UtmCampaign { get; private set; }

        [JsonProperty("utm_content", NullValueHandling = NullValueHandling.Ignore)]
        public string UtmContent { get; private set; }

        [JsonProperty("utm_medium", NullValueHandling = NullValueHandling.Ignore)]
        public string UtmMedium { get; private set; }

        [JsonProperty("utm_source", NullValueHandling = NullValueHandling.Ignore)]
        public string UtmSource { get; private set; }

        [JsonProperty("utm_term", NullValueHandling = NullValueHandling.Ignore)]
        public string UtmTerm { get; private set; }

        [JsonProperty("page_title", NullValueHandling = NullValueHandling.Ignore)]
        public string PageTitle { get; private set; }

        [JsonProperty("sdk_language")]
        public string SdkLanguage { get; private set; } = SdkMetadata.Language;

        [JsonProperty("sdk_version")]
        public string SdkVersion { get; private set; } = SdkMetadata.Version;

        /// <summary>Empty: plugins are not told the page URL.</summary>
        [JsonProperty("url")]
        public string Url { get; private set; } = string.Empty;

        /// <summary>The attributes not lifted to their own fields.</summary>
        [JsonProperty("context_json")]
        public JObject Context { get; private set; }

        /// <summary>The user's attributes as the plugin was given them. Not sent as such.</summary>
        [JsonIgnore]
        public JObject Attributes { get; private set; }

        /// <summary>
        /// The key the tracking plugin deduplicated this event under, so a batch that is never sent can
        /// be forgotten again. Not sent.
        /// </summary>
        [JsonIgnore]
        internal string DedupeKey { get; set; }

        private TrackingEvent() {}

        /// <summary>
        /// Creates a tracking event for an experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated.</param>
        /// <param name="result">The result of the assignment.</param>
        /// <param name="attributes">User attributes to include in the event.</param>
        public static TrackingEvent ForExperiment(Experiment experiment, ExperimentResult result, JObject attributes)
        {
            return Create(EventExperimentViewed, new JObject
            {
                ["experimentId"] = experiment?.Key,
                ["variationId"] = result?.Key,
                ["hashAttribute"] = result?.HashAttribute,
                ["hashValue"] = result?.HashValue
            }, attributes);
        }

        /// <summary>
        /// Creates a tracking event for a feature flag evaluation.
        /// </summary>
        /// <param name="featureKey">The key of the evaluated feature.</param>
        /// <param name="result">The result of the feature evaluation.</param>
        /// <param name="attributes">User attributes to include in the event.</param>
        public static TrackingEvent ForFeature(string featureKey, FeatureResult result, JObject attributes)
        {
            return Create(EventFeatureEvaluated, new JObject
            {
                ["feature"] = featureKey,
                ["source"] = result?.Source,
                ["value"] = result?.Value,
                ["variationId"] = result?.ExperimentResult?.Key ?? string.Empty
            }, attributes);
        }

        private static TrackingEvent Create(string eventName, JObject properties, JObject attributes)
        {
            attributes = attributes ?? new JObject();

            var context = (JObject)attributes.DeepClone();

            foreach (var name in IdentifyingAttributes)
            {
                context.Remove(name);
            }

            return new TrackingEvent
            {
                EventName = eventName,
                Properties = properties,
                Attributes = attributes,
                UserId = AsString(attributes["user_id"]),
                DeviceId = AsString(FirstTruthy(attributes["device_id"], attributes["anonymous_id"], attributes["id"])),
                PageId = AsString(attributes["page_id"]),
                SessionId = AsString(attributes["session_id"]),
                UtmCampaign = NonEmpty(attributes["utmCampaign"]),
                UtmContent = NonEmpty(attributes["utmContent"]),
                UtmMedium = NonEmpty(attributes["utmMedium"]),
                UtmSource = NonEmpty(attributes["utmSource"]),
                UtmTerm = NonEmpty(attributes["utmTerm"]),
                PageTitle = NonEmpty(attributes["pageTitle"]),
                Context = context
            };
        }

        /// <summary>The value if it is a string, as <c>parseString</c> in the JavaScript SDK; null otherwise.</summary>
        private static string AsString(JToken token) => token?.Type == JTokenType.String ? token.Value<string>() : null;

        /// <summary>The value if it is a non-empty string, left out otherwise.</summary>
        private static string NonEmpty(JToken token)
        {
            var value = AsString(token);

            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>The first token JavaScript would treat as true, as <c>a || b || c</c> picks it.</summary>
        private static JToken FirstTruthy(params JToken[] tokens)
        {
            foreach (var token in tokens)
            {
                if (new FeatureResult { Value = token }.On)
                {
                    return token;
                }
            }

            return null;
        }
    }
}
