using System;
using System.Collections.Generic;
using System.Linq;
using GrowthBook.Extensions;
using GrowthBook.MultiUser.Configuration;
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

                var forcedValues = context.GetForcedFeatureValues();
                if (forcedValues != null && forcedValues.TryGetValue(featureId, out var forcedValue))
                {
                    return GetFeatureResult(forcedValue, "override");
                }

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

                            var isSuccess = _conditionEvaluator.EvalCondition(evaluationObject,
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

                    if (rule.Filters?.Any() == true && ExperimentUtilities.IsFilteredOut(rule.Filters, context.GetAttributes()))
                    {
                        continue;
                    }

                    if (!rule.Condition.IsNull() &&
                        !_conditionEvaluator.EvalCondition(context.GetAttributes(), rule.Condition, context.Global.SavedGroups))
                    {
                        _logger.LogDebug("Rule {RuleIndex}: attribute condition did not match, continuing", ruleIndex);
                        continue;
                    }

                    if (!rule.Force.IsNull())
                    {
                        if (!ExperimentUtilities.IsIncludedInRollout(rule.Seed ?? featureId, context.GetAttributes(), rule.HashAttribute, rule.Range, rule.Coverage,
                                rule.HashVersion))
                        {
                            _logger.LogDebug("Rule {RuleIndex}: excluded by rollout/coverage, continuing", ruleIndex);
                            continue;
                        }

                        if (rule.Tracks?.Any() == true)
                        {
                            foreach (var trackData in rule.Tracks)
                            {
                                try
                                {
                                    context.Global.TrackingCallback?.Invoke(trackData.Experiment, trackData.Result);
                                    context.User.TrackingCallback?.Invoke(trackData.Experiment, trackData.Result);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex,
                                        $"Encountered unhandled exception in tracking callback for feature ID '{featureId}'");
                                }
                            }
                        }

                        // Subscribers hear about a forced evaluation too; there is no experiment behind
                        // it, which is what the null stands for.
                        context.Global.OnExperimentEval?.Invoke(null, new ExperimentResult
                        {
                            InExperiment = false,
                            Value = rule.Force
                        });

                        _logger.LogDebug("Rule {RuleIndex}: returning forced value for feature '{FeatureId}'",
                            ruleIndex, featureId);
                        return GetFeatureResult(rule.Force, FeatureResult.SourceId.Force);
                    }

                    var experiment = new Experiment
                    {
                        Variations = rule.Variations,
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

                    var result = new ExperimentEvaluationProvider(_logger, _conditionEvaluator).RunExperiment(experiment, featureId, context);

                    context.Global.OnExperimentEval?.Invoke(experiment, result);

                    if (!result.InExperiment || result.Passthrough)
                    {
                        continue;
                    }

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
