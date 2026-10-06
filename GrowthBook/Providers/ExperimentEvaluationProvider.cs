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
    internal sealed class ExperimentEvaluationProvider
    {
        private readonly ILogger _logger;
        private readonly IConditionEvaluationProvider _conditionEvaluator;

        public ExperimentEvaluationProvider(ILogger logger, IConditionEvaluationProvider conditionEvaluator)
        {
            _logger = logger;
            _conditionEvaluator = conditionEvaluator;
        }

        public ExperimentResult RunExperiment(Experiment experiment, string featureId, EvaluationContext context)
        {
            // 1. Abort if there aren't enough variations present.

            if (experiment.Variations.IsNull() || experiment.Variations.Count < 2)
            {
                _logger.LogDebug("Aborting experiment, not enough variations are present");
                return GetExperimentResult(experiment, context, featureId: featureId);
            }

            // 2. Abort if GrowthBook is currently disabled.

            if (!context.Global.Enabled)
            {
                _logger.LogDebug("Aborting experiment, GrowthBook is not currently enabled");
                return GetExperimentResult(experiment, context, featureId: featureId);
            }

            // NOTE: The improved URL targeting mentioned is only applicable on the front end.
            //       There are potential frontend usages for the C# SDK, but until there is more clarity and more robust tests
            //       in the JSON test suite to ensure we get an appropriate implementation in place we are going to hold off on this.

            // 2.6 Use improved URL targeting if specified.

            //if (experiment.UrlPatterns?.Count > 0 && !ExperimentUtilities.IsUrlTargeted(Url ?? string.Empty, experiment.UrlPatterns))
            //{
            //    _logger.LogDebug("Skipping due to URL targeting");
            //    return GetExperimentResult(experiment, featureId: featureId);
            //}

            // 3. Use the override value from the query string if one is specified.

            if (!context.User.Url.IsNullOrWhitespace())
            {
                var overrideValue = ExperimentUtilities.GetQueryStringOverride(experiment.Key, context.User.Url, experiment.Variations.Count);

                if (overrideValue != null)
                {
                    _logger.LogDebug("Found an override value in the query string, creating experiment result from it");
                    return GetExperimentResult(experiment, context, overrideValue.Value, featureId: featureId);
                }
            }

            // 4. Use the forced variation value instead if one is specified for this experiment.

            var forcedVariations = context.GetForcedVariations();

            if (forcedVariations.TryGetValue(experiment.Key, out var variation))
            {
                _logger.LogDebug("Found a forced variation value, creating experiment result from it");
                return GetExperimentResult(experiment, context, variation, featureId: featureId);
            }

            // 5. Abort if the experiment isn't currently active.

            if (!experiment.Active)
            {
                _logger.LogDebug("Aborting experiment, experiment is not currently active");
                return GetExperimentResult(experiment, context, featureId: featureId);
            }

            // 6. Abort if we're unable to generate a hash identifying this run.

            (var hashAttribute, var hashValue) = context.GetAttributes().GetHashAttributeAndValue(experiment.HashAttribute);

            if (hashValue.IsNullOrWhitespace())
            {
                // Check if a fallback attribute for sticky bucketing exists and use it if possible.

                var hasFallback = !experiment.FallbackAttribute.IsNullOrWhitespace();

                if (hasFallback)
                {
                    (hashAttribute, hashValue) = context.GetAttributes().GetHashAttributeAndValue(experiment.FallbackAttribute);
                }
                else
                {
                    _logger.LogDebug("Aborting experiment, unable to locate a value for the experiment hash attribute \'{ExperimentHashAttribute}\'", experiment.HashAttribute);
                    return GetExperimentResult(experiment, context, featureId: featureId);
                }
            }

            // 6.5 When sticky bucketing is permitted, determine if they already have a value and use it if possible.

            var assignedBucket = -1;
            var foundStickyBucket = false;
            var stickyBucketVersionIsBlocked = false;

            if (context.Global.StickyBucketService != null && !experiment.DisableStickyBucketing)
            {
                var bucketVersion = experiment.BucketVersion;
                var minBucketVersion = experiment.MinBucketVersion;
                var meta = experiment.Meta ?? new List<VariationMeta>();

                var stickyBucketVariation = ExperimentUtilities.GetStickyBucketVariation(
                    experiment,
                    bucketVersion,
                    minBucketVersion,
                    meta,
                    context.GetAttributes(),
                    context.User.StickyBucketAssignmentDocs
                );

                foundStickyBucket = stickyBucketVariation.VariationIndex >= 0;
                assignedBucket = stickyBucketVariation.VariationIndex;
                stickyBucketVersionIsBlocked = stickyBucketVariation.IsVersionBlocked;
            }

            if (!foundStickyBucket)
            {
                // 7. Abort if this run is ineligible to be included in the experiment.

                if (experiment.Filters?.Any() == true)
                {
                    if (ExperimentUtilities.IsFilteredOut(experiment.Filters, context.GetAttributes()))
                    {
                        _logger.LogDebug("Aborting experiment, filters have been applied and matched this run");
                        return GetExperimentResult(experiment, context, featureId: featureId);
                    }
                }
                else if (experiment.Namespace != null && !ExperimentUtilities.InNamespace(hashValue, experiment.Namespace))
                {
                    _logger.LogDebug("Aborting experiment, not within the specified namespace \'{ExperimentNamespace}\'", experiment.Namespace);
                    return GetExperimentResult(experiment, context, featureId: featureId);
                }

                // 8. Abort if the conditions for the experiment prohibit this.

                if (!experiment.Condition.IsNull())
                {
                    if (!_conditionEvaluator.EvalCondition(context.GetAttributes(), experiment.Condition, context.Global.SavedGroups))
                    {
                        _logger.LogDebug("Aborting experiment, associated conditions have prohibited participation");
                        return GetExperimentResult(experiment, context, featureId: featureId);
                    }
                }

                if (experiment.ParentConditions != null)
                {
                    var snapshot = new HashSet<string>(context.Stack.EvaluatedFeatures);

                    foreach (var parentCondition in experiment.ParentConditions)
                    {
                        context.Stack.EvaluatedFeatures = new HashSet<string>(snapshot);
                        var parentResult = new FeatureEvaluationProvider(_logger, _conditionEvaluator).EvaluateFeature(parentCondition.Id, context);
                        // Use a fresh copy of the evaluated feature ids to avoid
                        // incorrectly flagging repeated prerequisite evaluations as cycles

                        if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                        {
                            return GetExperimentResult(experiment, context, featureId: featureId);
                        }

                        var evaluationObject = new JObject { ["value"] = parentResult.Value };

                        if (!_conditionEvaluator.EvalCondition(evaluationObject, parentCondition.Condition ?? new JObject(), context.Global.SavedGroups))
                        {
                            return GetExperimentResult(experiment, context, featureId: featureId);
                        }
                    }
                }
            }

            // 9. Attempt to assign this run to an experiment variation.

            var hash = HashUtilities.Hash(experiment.Seed ?? experiment.Key, hashValue, experiment.HashVersion);

            if (hash is null)
            {
                return GetExperimentResult(experiment, context, featureId: featureId);
            }

            if (!foundStickyBucket)
            {
                var ranges = experiment.Ranges?.Count > 0 ? experiment.Ranges : ExperimentUtilities.GetBucketRanges(experiment.Variations?.Count ?? 0, experiment.Coverage ?? 1, experiment.Weights ?? new List<double>());
                assignedBucket = ExperimentUtilities.ChooseVariation(hash.Value, ranges.ToList());

                // 10. Abort if a variation could not be assigned.

                if (assignedBucket == -1)
                {
                    _logger.LogDebug("Aborting experiment, unable to assign this run to an experiment variation");
                    return GetExperimentResult(experiment, context, featureId: featureId);
                }
            }

            // 9.5 Unenroll if any prior sticky buckets are blocked by version.

            if (stickyBucketVersionIsBlocked)
            {
                return GetExperimentResult(experiment, context, featureId: featureId, wasStickyBucketUsed: true);
            }

            // 11. Use the forced value for the experiment if one is specified.

            if (experiment.Force != null)
            {
                _logger.LogDebug("Found a forced value, creating experiment result from it");
                return GetExperimentResult(experiment, context, experiment.Force.Value, featureId: featureId);
            }

            // 12. Abort if we're currently operating in QA mode.

            if (context.Global.QaMode)
            {
                _logger.LogDebug("Aborting experiment, this run is in QA mode");
                return GetExperimentResult(experiment, context, featureId: featureId);
            }

            // 13. Run the experiment and track the result if we haven't seen this one before.

            _logger.LogInformation("Participation in experiment with key \'{ExperimentKey}\' is allowed, running the experiment", experiment.Key);
            var result = GetExperimentResult(experiment, context, assignedBucket, true, featureId, hash, foundStickyBucket);

            // 13.5 Store the value for later if sticky bucketing is enabled.

            if (context.Global.StickyBucketService != null && !experiment.DisableStickyBucketing)
            {
                var experimentKey = ExperimentUtilities.GetStickyBucketExperimentKey(experiment.Key, experiment.BucketVersion);

                var assignments = new Dictionary<string, string>
                {
                    [experimentKey] = result.Key
                };

                (var document, var isChanged) = ExperimentUtilities.GenerateStickyBucketAssignment(context.Global.StickyBucketService, hashAttribute, hashValue, assignments);

                if (isChanged)
                {
                    context.Global.StickyBucketService.SaveAssignments(document);
                    context.User.StickyBucketAssignmentDocs[document.FormattedAttribute] = document;
                }
            }

            // The assignment is already decided and may already be saved, so a throwing callback must
            // not change what the user is shown. Elsewhere in evaluation these are guarded the same way.
            try
            {
                context.Global.TrackingCallback?.Invoke(experiment, result);
                context.User.TrackingCallback?.Invoke(experiment, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encountered unhandled exception in tracking callback for experiment '{ExperimentKey}'", experiment.Key);
            }

            return result;
        }

        /// <summary>
        /// Generates an experiment result from an experiment.
        /// </summary>
        /// <param name="experiment">The experiment to get the result from.</param>
        /// <param name="variationIndex">The variation id, if specified.</param>
        /// <param name="hashUsed">Whether or not a hash was used in assignment.</param>
        /// <returns>The experiment result.</returns>
        private ExperimentResult GetExperimentResult(Experiment experiment, EvaluationContext context, int variationIndex = -1, bool hashUsed = false, string featureId = null, double? bucketHash = null, bool wasStickyBucketUsed = false)
        {
            var inExperiment = true;

            if (variationIndex < 0 || variationIndex >= experiment.Variations.Count)
            {
                variationIndex = 0;
                inExperiment = false;
            }

            var canUseStickyBucketing = context.Global.StickyBucketService != null && !experiment.DisableStickyBucketing;
            var fallbackAttribute = canUseStickyBucketing ? experiment.FallbackAttribute : default;

            (var hashAttribute, var hashValue) = context.GetAttributes().GetHashAttributeAndValue(experiment.HashAttribute, fallbackAttributeKey: fallbackAttribute);

            var meta = experiment.Meta?.Count > 0 ? experiment.Meta[variationIndex] : null;

            var result = new ExperimentResult
            {
                Key = meta?.Key ?? variationIndex.ToString(),
                FeatureId = featureId,
                InExperiment = inExperiment,
                HashAttribute = hashAttribute,
                HashUsed = hashUsed,
                HashValue = hashValue,
                Value = experiment.Variations is null ? null : experiment.Variations[variationIndex],
                VariationId = variationIndex,
                Name = meta?.Name,
                Passthrough = meta?.Passthrough ?? false,
                Bucket = bucketHash ?? 0d,
                StickyBucketUsed = wasStickyBucketUsed
            };

            return result;
        }


    }
}
