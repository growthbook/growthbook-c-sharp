using System;
using System.Collections.Generic;
using GrowthBook.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.MultiUser.Configuration
{
    /// <summary>Singleton client for multiuser feature flagging. Register as a singleton in DI.</summary>
    public class Options
    {
        /// <summary>The GrowthBook SDK key used to fetch features from the API.</summary>
        public string ClientKey { get; set; }

        /// <summary>The GrowthBook API host. Defaults to https://cdn.growthbook.io.</summary>
        public string ApiHost { get; set; } = "https://cdn.growthbook.io";

        /// <summary>Optional decryption key. If set, the features payload is expected to be encrypted.</summary>
        public string DecryptionKey { get; set; }

        /// <summary>Whether all experiments are globally enabled. Set to false to disable all experiments. Defaults to true.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>If true, random assignment is disabled and only explicitly forced variations are used.</summary>
        public bool QaMode { get; set; }

        /// <summary>Attributes applied to every user evaluation. Per-request <see cref="UserContext.Attributes"/> take precedence.</summary>
        public JObject GlobalAttributes { get; set; }

        /// <summary>
        /// Saved group definitions, used by <c>$inGroup</c> and <c>$notInGroup</c> conditions.
        /// </summary>
        /// <remarks>
        /// Without these, every condition that names a saved group evaluates to false for every
        /// user - the feature targets correctly in the dashboard and silently does not here.
        /// <para>
        /// They have to be supplied because the feature payload does not surface them: the
        /// repository returns features alone, so there is nowhere for the <c>savedGroups</c> the API
        /// sends to arrive. The JavaScript SDK takes them from the payload and keeps them in its
        /// options, which is where this should end up once the repository can hand over more than
        /// the features.
        /// </para>
        /// </remarks>
        public JObject SavedGroups { get; set; }

        /// <summary>Force specific experiments to always assign a specific variation for all users. Used for QA.</summary>
        public IDictionary<string, int> GlobalForcedVariations { get; set; }

        /// <summary>Logger factory for internal diagnostics. If not set, a no-op logger is used.</summary>
        public ILoggerFactory LoggerFactory { get; set; }

        /// <summary>Custom feature repository. If set, <see cref="ClientKey"/> and <see cref="ApiHost"/> are ignored.</summary>
        public IGrowthBookFeatureRepository FeatureRepository { get; set; }

        /// <summary>
        /// Invoked after features are loaded or refreshed, with true on success and false on failure.
        /// A response carrying no change - HTTP 304 - applies nothing and so fires nothing.
        /// </summary>
        public Action<bool> OnFeaturesRefreshed { get; set; }

        /// <summary>
        /// Enable background streaming updates via SSE. When true, the client keeps a persistent
        /// connection to the GrowthBook streaming endpoint and automatically refreshes features
        /// without an explicit <see cref="GrowthBookClient.RefreshFeaturesAsync"/> call.
        /// Defaults to false (opt-in), consistent with the single-user <see cref="Context.BackgroundSync"/>.
        /// </summary>
        public bool BackgroundSync { get; set; } = false;

        /// <summary>
        /// How long in seconds before the feature cache is considered expired and a refresh is triggered.
        /// Defaults to 60.
        /// </summary>
        public int CacheExpirationInSeconds { get; set; } = 60;

        /// <summary>
        /// Timeout in seconds for HTTP requests to the GrowthBook API (both polling and SSE).
        /// Defaults to 60.
        /// </summary>
        public int HttpRequestTimeoutInSeconds { get; set; } = 60;

        /// <summary>
        /// Invoked when a user is assigned to an experiment variation. Use to report to your analytics system.
        /// An assignment is reported once per evaluation call; to report it once per request however many
        /// evaluations it makes, give each <see cref="UserContext"/> a
        /// <see cref="UserContext.TrackedExperiments"/> set that lives as long as the request.
        /// </summary>
        public Action<Experiment, ExperimentResult> TrackingCallback { get; set; }

        /// <summary>Service that provides sticky bucketing to ensure consistent experiment assignments across sessions.</summary>
        public IStickyBucketService StickyBucketService { get; set; }
    }
}
