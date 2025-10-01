using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Overrides the defaultValue of a Feature.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class FeatureRule
    {
        /// <summary>
        /// Optional rule id, reserved for future use.
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Optional targeting condition.
        /// </summary>
        public JObject? Condition { get; set; }

        /// <summary>
        /// Each item defines a prerequisite where a condition must evaluate against a parent feature's value (identified by id). If gate is true, then this is a blocking feature-level prerequisite; otherwise it applies to the current rule only.
        /// </summary>
        public IList<ParentCondition>? ParentConditions { get; set; }

        /// <summary>
        /// What percent of users should be included in the experiment (between 0 and 1, inclusive).
        /// </summary>
        public double? Coverage { get; set; }

        /// <summary>
        /// Immediately force a specific value (ignore every other option besides condition and coverage).
        /// </summary>
        public JToken? Force { get; set; }

        /// <summary>
        /// Run an experiment (A/B test) and randomly choose between these variations.
        /// </summary>
        public JArray? Variations { get; set; }

        /// <summary>
        /// The globally unique tracking key for the experiment (default to the feature key).
        /// </summary>
        public string? Key { get; set; }

        /// <summary>
        /// How to weight traffic between variations. Must add to 1.
        /// </summary>
        public IList<double>? Weights { get; set; }

        /// <summary>
        /// Adds the experiment to a namespace.
        /// </summary>
        public Namespace? Namespace { get; set; }

        /// <summary>
        /// What user attribute should be used to assign variations (defaults to id).
        /// </summary>
        public string HashAttribute { get; set; } = "id";

        /// <summary>
        /// When using sticky bucketing, can be used as a fallback to assign variations.
        /// </summary>
        public string? FallbackAttribute { get; set; }

        /// <summary>
        /// The hash version to use (defaults to 1).
        /// </summary>
        public int HashVersion { get; set; } = 1;

        /// <summary>
        /// A more precise version of Coverage.
        /// </summary>
        public BucketRange? Range { get; set; }

        /// <summary>
        /// If true, sticky bucketing will be disabled for this experiment. (Note: sticky bucketing is only available if a StickyBucketingService is provided in the Context).
        /// </summary>
        public bool DisableStickyBucketing { get; set; }

        /// <summary>
        /// A sticky bucket version number that can be used to force a re-bucketing of users (default to 0).
        /// </summary>
        public int BucketVersion { get; set; }

        /// <summary>
        /// Any users with a sticky bucket version less than this will be excluded from the experiment.
        /// </summary>
        public int MinBucketVersion { get; set; }

        /// <summary>
        /// Ranges for experiment variations.
        /// </summary>
        public IList<BucketRange>? Ranges { get; set; }

        /// <summary>
        /// Meta info about the experiment variations.
        /// </summary>
        public IList<VariationMeta>? Meta { get; set; }

        /// <summary>
        /// Array of filters to apply to the rule.
        /// </summary>
        public IList<Filter>? Filters { get; set; }

        /// <summary>
        /// Seed to use for hashing.
        /// </summary>
        public string? Seed { get; set; }

        /// <summary>
        /// Human-readable name for the experiment.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// The phase id of the experiment.
        /// </summary>
        public string? Phase { get; set; }

        /// <summary>
        /// Array of tracking calls to fire.
        /// </summary>
        public IList<TrackData>? Tracks { get; set; }

        /// <summary>
        /// Returns the feature variations cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The variations cast as the specified type.</returns>
        public T GetVariations<T>()
        {
            if (Variations == null)
            {
                throw new InvalidOperationException("Variations is null.");
            }
            return Variations.ToObject<T>()!;
        }
    }
}
