using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    /// <summary>
    /// Represents a set of configuration values that can affect how a
    /// particular <see cref="IGrowthBookFeatureRepository"/> implementation gets
    /// data from the API and how it handles it once it's retrieved.
    /// </summary>
    public class GrowthBookConfigurationOptions
    {
        /// <summary>
        /// The GrowthBook API Host. Optional. Defaults to the GrowthBook CDN.
        /// </summary>
        public string ApiHost { get; set; } = "https://cdn.growthbook.io";

        /// <summary>
        /// The number of seconds from a cache refresh to the cache becoming expired. Optional. Defaults to 60.
        /// </summary>
        public int CacheExpirationInSeconds { get; set; } = 60;

        /// <summary>
        /// The key used to fetch features from the GrowthBook API. Required.
        /// </summary>
        public string ClientKey { get; set; }

        /// <summary>
        /// The key used to decrypt encrypted features from the API. Optional unless you use encrypted features.
        /// </summary>
        public string DecryptionKey { get; set; }

        /// <summary>
        /// In the case where server sent events are available from the API, which would indicate a near-realtime
        /// feature retrieval cadence is possible, that should be opted into and used going forward
        /// in lieu of an explicit call to refresh through the API.
        /// </summary>
        public bool PreferServerSentEvents { get; set; }

        /// <summary>
        /// When true, enables background streaming (SSE) for near real-time updates.
        /// Alias for PreferServerSentEvents for semantic clarity.
        /// </summary>
        public bool BackgroundSync
        {
            get => PreferServerSentEvents;
            set => PreferServerSentEvents = value;
        }

        /// <summary>
        /// Optional custom headers to include on feature fetch (polling/manual refresh) requests.
        /// </summary>
        public IDictionary<string, string> RequestHeaders { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Optional custom headers to include on streaming (SSE) connection requests.
        /// For example: Authorization and Last-Event-ID to resume after restarts.
        /// </summary>
        public IDictionary<string, string> StreamingRequestHeaders { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Optional callback fired after features are applied to the cache.
        /// The boolean parameter indicates success (true) or failure (false).
        /// Fires for both streaming updates and manual refreshes.
        /// </summary>
        public Action<bool> OnFeaturesRefreshed { get; set; }

        /// <summary>
        /// Callback providing the latest SSE Last-Event-ID so apps can persist it.
        /// </summary>
        public Action<string> OnStreamingEventId { get; set; }
    }
}
