using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents a single experiment with multiple variations.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class Experiment
    {
        /// <summary>
        /// The globally unique identifier for the experiment.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The different variations to choose between.
        /// </summary>
        public JArray Variations { get; set; }

        /// <summary>
        /// How to weight traffic between variations. Must add to 1.
        /// </summary>
        public IList<double> Weights { get; set; }

        /// <summary>
        /// If set to false, always return the control (first variation).
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// What percent of users should be included in the experiment (between 0 and 1, inclusive).
        /// </summary>
        public double? Coverage { get; set; }

        /// <summary>
        ///  Array of ranges, one per variation.
        /// </summary>
        public IList<BucketRange> Ranges { get; set; }

        /// <summary>
        /// Optional targeting condition.
        /// </summary>
        public JObject Condition { get; set; }

        /// <summary>
        /// Each item defines a prerequisite where a condition must evaluate against a parent feature's value (identified by id). If gate is true, then this is a blocking feature-level prerequisite; otherwise it applies to the current rule only.
        /// </summary>
        public IList<ParentCondition> ParentConditions { get; set; }

        /// <summary>
        /// Adds the experiment to a namespace.
        /// </summary>
        public Namespace Namespace { get; set; }

        /// <summary>
        /// All users included in the experiment will be forced into the specific variation index.
        /// </summary>
        public int? Force { get; set; }

        /// <summary>
        /// What user attribute should be used to assign variations (defaults to id).
        /// </summary>
        public string HashAttribute { get; set; } = "id";

        /// <summary>
        /// When using sticky bucketing, can be used as a fallback to assign variations.
        /// </summary>
        public string FallbackAttribute { get; set; }

        /// <summary>
        /// The hash version to use (defaults to 1).
        /// </summary>
        public int HashVersion { get; set; } = 1;

        /// <summary>
        /// Meta info about the variations.
        /// </summary>
        public IList<VariationMeta> Meta { get; set; }

        /// <summary>
        /// Array of filters to apply.
        /// </summary>
        public IList<Filter> Filters { get; set; }

        /// <summary>
        /// The hash seed to use.
        /// </summary>
        public string Seed { get; set; }

        /// <summary>
        /// Human-readable name for the experiment.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// ID of the current experiment phase.
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// If true, sticky bucketing will be disabled for this experiment. (Note: sticky bucketing is only available if a StickyBucketingService is provided in the Context).
        /// </summary>
        public bool DisableStickyBucketing { get; set; }

        /// <summary>
        /// A sticky bucket version number that can be used to force a re-bucketing of users (default to 0).
        /// </summary>
        public int BucketVersion { get; set; } = 0;

        /// <summary>
        /// Any users with a sticky bucket version less than this will be excluded from the experiment.
        /// </summary>
        public int MinBucketVersion { get; set; } = 0;

        /// <summary>
        /// Any URL patterns associated with this experiment.
        /// </summary>
        public IList<UrlPattern> UrlPatterns { get; set; }

        /// <summary>
        /// Determines whether to persist the query string.
        /// </summary>
        public bool PersistQueryString { get; set; }

        /// <summary>
        /// Returns the experiment variations cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The variations cast as the specified type.</returns>
        public T GetVariations<T>()
        {
            return Variations.ToObject<T>();
        }

        public override bool Equals(object obj)
        {
            if (obj.GetType() == typeof(Experiment))
            {
                Experiment objExp = (Experiment)obj;
                return Active == objExp.Active
                    && JToken.DeepEquals(Condition, objExp.Condition)
                    && Coverage == objExp.Coverage
                    && Force == objExp.Force
                    && HashAttribute == objExp.HashAttribute
                    && Key == objExp.Key
                    && object.Equals(Namespace, objExp.Namespace)
                    && JToken.DeepEquals(Variations, objExp.Variations)
                    && ((Weights == null && objExp.Weights == null) || (Weights == null || objExp.Weights == null ? false : Weights.SequenceEqual(objExp.Weights)));
            }
            return false;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
