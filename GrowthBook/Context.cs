using System;
using System.Collections.Generic;
using GrowthBook.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents a parameter object passed into the GrowthBook constructor.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class Context
    {
        /// <summary>
        /// Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The GrowthBook API Host. Optional.
        /// </summary>
        public string ApiHost { get; set; }

        /// <summary>
        /// The key used to fetch features from the GrowthBook API. Optional.
        /// </summary>
        public string ClientKey { get; set; }

        /// <summary>
        /// The key used to decrypt encrypted features from the API. Optional.
        /// </summary>
        public string DecryptionKey { get; set; }

        /// <summary>
        /// Map of user attributes that are used to assign variations.
        /// </summary>
        public JObject Attributes { get; set; } = new JObject();

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Feature definitions (usually pulled from an API or cache).
        /// </summary>
        public IDictionary<string, Feature> Features { get; set; } = new Dictionary<string, Feature>();

        /// <summary>
        /// Experiment definitions.
        /// </summary>
        public IList<Experiment> Experiments { get; set; }

        /// <summary>
        /// Service for using sticky buckets.
        /// </summary>
        public IStickyBucketService StickyBucketService { get; set; }

        /// <summary>
        /// The assignment docs for sticky bucket usage. Optional.
        /// </summary>
        public IDictionary<string, StickyAssignmentsDocument> StickyBucketAssignmentDocs { get; set; } = new Dictionary<string, StickyAssignmentsDocument>();

        /// <summary>
        /// Feature definitions that have been encrypted. Requires that the <see cref="DecryptionKey"/> property
        /// be set in order for the <see cref="GrowthBook"/> class to decrypt them for use.
        /// </summary>
        public string EncryptedFeatures { get; set; }

        /// <summary>
        /// Force specific experiments to always assign a specific variation (used for QA).
        /// </summary>
        public IDictionary<string, int> ForcedVariations { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// Gets groups that have been saved, if any.
        /// </summary>
        public JObject SavedGroups { get; set; }

        /// <summary>
        /// If true, random assignment is disabled and only explicitly forced variations are used.
        /// </summary>
        public bool QaMode { get; set; } = false;

        /// <summary>
        /// Callback function used for tracking Experiment assignment.
        /// </summary>
        public Action<Experiment, ExperimentResult> TrackingCallback { get; set; }

        /// <summary>
        /// A repository implementation for retrieving and caching features that will override
        /// the default implementation. Optional.
        /// </summary>
        public IGrowthBookFeatureRepository FeatureRepository { get; set; }

        /// <summary>
        /// A logger factory implementation that will enable logging throughout the SDK. Optional.
        /// </summary>
        public ILoggerFactory LoggerFactory { get; set; }

        /// <summary>
        /// Custom cache directory path for cache manager. Optional.
        /// Uses system temp directory if not specified.
        /// </summary>
        public string CachePath { get; set; }
    }
}
