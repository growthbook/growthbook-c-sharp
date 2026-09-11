using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// A single leaf of a <see cref="ContextualBanditDefinition"/>: a targeting condition paired with the
    /// variation weights to apply when that condition matches the user.
    /// </summary>
    /// <remarks>
    /// Part of the read-only contextual bandit payload. Leaves are supplied by the backend and never modified
    /// during evaluation.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ContextualBanditContext
    {
        /// <summary>
        /// Identifier of this leaf within the definition. Reported on <see cref="ExperimentResult"/> so an exposure
        /// can be attributed back to the segment whose weights produced it.
        /// </summary>
        public int? LeafId { get; set; }

        /// <summary>
        /// Targeting condition evaluated against the user's attributes. A null or empty condition matches everyone,
        /// which is how a catch-all leaf is expressed.
        /// </summary>
        public JObject Condition { get; set; }

        /// <summary>
        /// Variation weights to apply when this leaf matches. Aligns positionally with the experiment's variations
        /// and, like any weights, sums to 1.
        /// </summary>
        public IList<double> Weights { get; set; }
    }

    /// <summary>
    /// A contextual bandit definition, keyed in the payload by the <see cref="FeatureRule.ContextualBanditRef"/>
    /// that a feature rule points at.
    /// </summary>
    /// <remarks>
    /// A contextual bandit assigns variation weights per user segment: each leaf carries a targeting condition and
    /// the weights to use when it matches. The weights themselves are computed on the GrowthBook backend - the SDK
    /// only selects the matching leaf and applies its weights before bucketing.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ContextualBanditDefinition
    {
        /// <summary>
        /// Version of the backend-computed weights, reported on <see cref="ExperimentResult"/> so an exposure can be
        /// attributed to the weight generation that produced it.
        /// </summary>
        public int? BanditVersion { get; set; }

        /// <summary>
        /// Ordered leaves. They are evaluated top to bottom and the first whose condition matches the user wins.
        /// </summary>
        public IList<ContextualBanditContext> Contexts { get; set; }
    }

    /// <summary>
    /// The outcome of resolving a <see cref="ContextualBanditDefinition"/> for one user: the leaf that was selected
    /// and the weights that were applied to the experiment.
    /// </summary>
    /// <remarks>
    /// A <see cref="LeafId"/> of <see cref="FallbackLeafId"/> means a definition was found but no leaf matched, so the
    /// experiment kept its own marginal weights.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ContextualBandit
    {
        /// <summary>
        /// The leaf id recorded when a definition was found but none of its leaves matched the user.
        /// </summary>
        public const int FallbackLeafId = -1;

        /// <summary>
        /// The matched leaf's id, or <see cref="FallbackLeafId"/> when no leaf matched.
        /// </summary>
        public int? LeafId { get; set; }

        /// <summary>
        /// The weights actually applied to the experiment for this user.
        /// </summary>
        public IList<double> VariationWeights { get; set; }

        /// <summary>
        /// The bandit version of the definition that produced these weights.
        /// </summary>
        public int? BanditVersion { get; set; }
    }
}
