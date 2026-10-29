using System;
using System.Collections.Generic;
using System.Linq;
using GrowthBook.Extensions;
using GrowthBook.Evaluation;
using GrowthBook.Utilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Providers
{
    internal sealed class FeatureEvaluationProvider
    {
        private readonly ILogger _logger;
        private readonly IConditionEvaluationProvider _conditionEvaluator;

        public FeatureEvaluationProvider(
            ILogger logger,
            IConditionEvaluationProvider conditionEvaluator)
        {
            _logger = logger;
            _conditionEvaluator = conditionEvaluator;
        }

        internal ExperimentEvaluationProvider ExperimentEvaluator { get; set; }

        public FeatureResult EvaluateFeature(string featureId, EvaluationContext context)
        {
            try
            {
                // evaluatedFeatures = evaluatedFeatures ?? new HashSet<string>();

                if (context.Stack.EvaluatedFeatures.Contains(featureId))
                {
                    return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                }

                context.Stack.EvaluatedFeatures.Add(featureId);

                if (!context.Global.Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                _logger.LogDebug("Evaluating feature '{FeatureId}' with {RuleCount} rules", featureId,
                    feature?.Rules?.Count ?? 0);

                var ruleIndex = 0;

                var snapshot = new HashSet<string>(context.Stack.EvaluatedFeatures);
                foreach (FeatureRule rule in feature?.Rules ?? Enumerable.Empty<FeatureRule>())
                {
                    ruleIndex++;
                    if (rule.ParentConditions != null)
                    {
                        var passedPrerequisiteEvaluations = true;

                        foreach (var parentCondition in rule.ParentConditions)
                        {
                            // Use a fresh copy of the evaluated feature ids to avoid
                            // incorrectly flagging repeated prerequisite evaluations as cycles
                            context.Stack.EvaluatedFeatures = new HashSet<string>(snapshot); // reset
                            var parentResult = EvaluateFeature(parentCondition.Id, context);

                            // Don't continue evaluating if the prerequisite conditions have cycles.
                            if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                            {
                                _logger.LogWarning(
                                    "Detected cyclic prerequisite while evaluating parent feature '{ParentId}' for feature '{FeatureId}'. Evaluated: {EvaluatedFeatures}",
                                    parentCondition.Id, featureId, string.Join(",", context.Stack.EvaluatedFeatures));
                                return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                            }

                            var evaluationObject = new JObject { ["value"] = parentResult.Value };

                            var isSuccess = EvalConditionSafely(evaluationObject,
                                parentCondition.Condition ?? new JObject(), context.Global.SavedGroups);

                            if (!isSuccess)
                            {
                                // When the parent evaluation is gated we'll treat that as a complete failure.

                                if (parentCondition.Gate)
                                {
                                    _logger.LogDebug(
                                        "Rule {RuleIndex}: Gated prerequisite '{ParentId}' failed for feature '{FeatureId}', aborting",
                                        ruleIndex, parentCondition.Id, featureId);
                                    return GetFeatureResult(default, FeatureResult.SourceId.Prerequisite);
                                }

                                passedPrerequisiteEvaluations = false;
                                _logger.LogDebug(
                                    "Rule {RuleIndex}: Prerequisite '{ParentId}' did not pass for feature '{FeatureId}', continuing to next rule",
                                    ruleIndex, parentCondition.Id, featureId);
                                break;
                            }
                        }

                        if (!passedPrerequisiteEvaluations)
                        {
                            continue;
                        }
                    }

                    if (rule.Filters?.Any() == true && ExperimentUtilities.IsFilteredOut(rule.Filters, context.GetAttributes(), _logger))
                    {
                        continue;
                    }

                    if (!rule.Condition.IsNull() &&
                        !EvalConditionSafely(context.GetAttributes(), rule.Condition, context.Global.SavedGroups))
                    {
                        _logger.LogDebug("Rule {RuleIndex}: attribute condition did not match, continuing", ruleIndex);
                        continue;
                    }

                    if (!rule.Force.IsNull())
                    {
                        // The fallback attribute is only used along with sticky bucketing, as in the JS SDK
                        var rolloutFallbackAttribute = context.Global.StickyBucketService != null && !rule.DisableStickyBucketing ? rule.FallbackAttribute : null;

                        if (!ExperimentUtilities.IsIncludedInRollout(rule.Seed ?? featureId, context.GetAttributes(), rule.HashAttribute, rolloutFallbackAttribute,
                                rule.Range, rule.Coverage, rule.HashVersion, _logger))
                        {
                            _logger.LogDebug("Rule {RuleIndex}: excluded by rollout/coverage, continuing", ruleIndex);
                            continue;
                        }

                        if (rule.Tracks?.Any() == true)
                        {
                            foreach (var trackData in rule.Tracks)
                            {
                                if (trackData?.Experiment == null || trackData.Result == null)
                                {
                                    continue;
                                }

                                ExperimentEvaluator.TrackExposure(trackData.Experiment, trackData.Result, context);
                            }
                        }

                        // Subscribers hear about a forced evaluation too; there is no experiment behind
                        // it, which is what the null stands for. Notification only - there is no
                        // assignment to record, and recording one would need an experiment key.
                        context.Global.OnSubscriberNotification?.Invoke(null, new ExperimentResult
                        {
                            InExperiment = false,
                            Value = rule.Force
                        });

                        _logger.LogDebug("Rule {RuleIndex}: returning forced value for feature '{FeatureId}'",
                            ruleIndex, featureId);
                        return GetFeatureResult(rule.Force, FeatureResult.SourceId.Force);
                    }

                    // A contextual bandit rule carries its variations under ContextualVariations so that an SDK
                    // without bandit support skips the rule outright. Read them first, whether or not the rule's
                    // reference resolves to a definition we have.
                    var experiment = new Experiment
                    {
                        Variations = rule.ContextualVariations ?? rule.Variations,
                        Key = rule.Key ?? featureId,
                        Coverage = rule.Coverage,
                        Weights = rule.Weights,
                        HashAttribute = rule.HashAttribute,
                        FallbackAttribute = rule.FallbackAttribute,
                        DisableStickyBucketing = rule.DisableStickyBucketing,
                        BucketVersion = rule.BucketVersion,
                        MinBucketVersion = rule.MinBucketVersion,
                        Namespace = rule.Namespace,
                        Meta = rule.Meta,
                        Ranges = rule.Ranges,
                        Name = rule.Name,
                        Phase = rule.Phase,
                        Seed = rule.Seed,
                        Filters = rule.Filters,
                        HashVersion = rule.HashVersion,
                        Condition = rule.Condition
                    };

                    // Resolved before bucketing, since it replaces the weights the user is bucketed against.
                    if (!string.IsNullOrEmpty(rule.ContextualBanditRef))
                    {
                        ApplyContextualBandit(experiment, rule.ContextualBanditRef, featureId, context);
                    }

                    var result = ExperimentEvaluator.RunExperiment(experiment, featureId, context);

                    // The bandit is attached before evaluation, so it has to be withdrawn from anything that wasn't a
                    // real exposure - a forced variation, QA mode, or being filtered out. Those assignments owe
                    // nothing to the bandit's weights and must not be attributed to them. A real exposure was
                    // already attributed inside RunExperiment, before the tracking callback saw it.
                    if (experiment.ContextualBandit != null && !(result.HashUsed && result.InExperiment))
                    {
                        experiment.ContextualBandit = null;
                    }

                    context.Global.OnExperimentAssigned?.Invoke(experiment, result);

                    if (!result.InExperiment || result.Passthrough)
                    {
                        continue;
                    }

                    context.Global.OnSubscriberNotification?.Invoke(experiment, result);

                    return GetFeatureResult(result.Value, FeatureResult.SourceId.Experiment, experiment, result);
                }

                _logger.LogDebug("No rules matched for feature '{FeatureId}', returning default value", featureId);
                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Encountered an unhandled exception while executing '{nameof(EvaluateFeature)}'");

                if (!context.Global.Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
        }

        /// <summary>
        /// Replaces an experiment's weights with those of the contextual bandit leaf that matches the current user.
        /// </summary>
        /// <remarks>
        /// All the optimisation happens on the GrowthBook backend; this only picks the segment and applies the weights
        /// it was given, then buckets through the ordinary hashing mechanism.
        /// </remarks>
        /// <param name="experiment">The experiment built from the rule, modified in place.</param>
        /// <param name="contextualBanditRef">The definition key the rule points at.</param>
        /// <param name="featureId">The feature being evaluated, for logging.</param>
        /// <param name="context">The evaluation the rule belongs to.</param>
        private void ApplyContextualBandit(Experiment experiment, string contextualBanditRef, string featureId, EvaluationContext context)
        {
            if (!context.Global.TryGetContextualBandit(contextualBanditRef, out var definition)
                || definition is null)
            {
                // Nothing to apply, so the rule runs on its own marginal weights and reports no bandit metadata.
                // That way a reference the payload hasn't caught up with degrades to an ordinary experiment.
                _logger.LogDebug("Contextual bandit '{ContextualBanditRef}' is not in the payload for feature '{FeatureId}', using the rule's own weights", contextualBanditRef, featureId);
                return;
            }

            var leaf = SelectContextualBanditLeaf(definition.Contexts, context);

            if (leaf?.Weights != null)
            {
                experiment.Weights = leaf.Weights;

                // Bucketing prefers explicit ranges over weights, so ranges carried by the rule would decide the
                // assignment while tracking reported the leaf's weights. Dropping them lets the leaf's weights be
                // turned into the ranges that actually select the variation.
                experiment.Ranges = null;
                experiment.ContextualBandit = new ContextualBandit
                {
                    LeafId = leaf.LeafId,
                    VariationWeights = leaf.Weights,
                    BanditVersion = definition.BanditVersion
                };

                return;
            }

            // A definition whose leaves all miss is still a bandit exposure, just on the fallback: keep the rule's own
            // weights and record the sentinel leaf, so tracking can tell "no segment matched" from "no bandit here".
            var fallbackWeights = experiment.Weights
                ?? ExperimentUtilities.GetEqualWeights(experiment.Variations?.Count ?? 0).ToList();

            experiment.Weights = fallbackWeights;
            experiment.ContextualBandit = new ContextualBandit
            {
                LeafId = ContextualBandit.FallbackLeafId,
                VariationWeights = fallbackWeights,
                BanditVersion = definition.BanditVersion
            };

            _logger.LogDebug("No contextual bandit leaf matched for feature '{FeatureId}', falling back to the rule's own weights", featureId);
        }

        /// <summary>
        /// Returns the first leaf whose condition matches the current user's attributes.
        /// </summary>
        /// <param name="contexts">The definition's leaves, in the order the backend supplied them.</param>
        /// <param name="context">The evaluation supplying the user's attributes and the saved groups.</param>
        /// <returns>The matching leaf, or null when none match.</returns>
        private ContextualBanditContext SelectContextualBanditLeaf(IList<ContextualBanditContext> contexts, EvaluationContext context)
        {
            if (contexts is null)
            {
                return null;
            }

            try
            {
                foreach (var leaf in contexts)
                {
                    // An absent or empty condition matches everyone, which is how a catch-all leaf is expressed.
                    if (_conditionEvaluator.EvalCondition(context.GetAttributes(), leaf.Condition ?? new JObject(), context.Global.SavedGroups))
                    {
                        return leaf;
                    }
                }
            }
            catch (Exception ex)
            {
                // The whole selection is abandoned rather than the offending leaf skipped. Skipping would let a later
                // leaf win that the reference SDKs never reach, so the same user would be bucketed on different
                // weights depending on which SDK evaluated them. Falling back is the one answer they all agree on.
                _logger.LogWarning(ex, "Contextual bandit leaf selection failed, falling back to the rule's own weights");
            }

            return null;
        }

        /// <summary>
        /// Evaluates a condition, failing it instead of throwing when it is malformed.
        /// </summary>
        private bool EvalConditionSafely(JToken attributes, JObject condition, JObject savedGroups)
        {
            try
            {
                return _conditionEvaluator.EvalCondition(attributes, condition, savedGroups);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Condition evaluation failed, treating the condition as not matching");
                return false;
            }
        }

        private FeatureResult GetFeatureResult(JToken value, string source, Experiment experiment = null,
            ExperimentResult experimentResult = null)
        {
            return new FeatureResult
            {
                Value = value, Source = source, Experiment = experiment, ExperimentResult = experimentResult
            };
        }
    }
}
