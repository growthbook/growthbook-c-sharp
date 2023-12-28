using System;
using System.Collections.Generic;
using System.Linq;
using GrowthBook.Extensions;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// This is the C# client library for GrowthBook, the open-source
    //  feature flagging and A/B testing platform.
    //  More info at https://www.growthbook.io
    /// </summary>
    public class GrowthBook : IGrowthBook, IDisposable
    {
        private readonly bool _qaMode;
        private readonly Dictionary<string, ExperimentAssignment> _assigned;
        private readonly HashSet<string> _tracked;
        private Action<Experiment, ExperimentResult> _trackingCallback;
        private readonly List<Action<Experiment, ExperimentResult>> _subscriptions;
        private bool _disposedValue;

        /// <summary>
        /// Creates a new GrowthBook instance from the passed context.
        /// </summary>
        /// <param name="context">The GrowthBook Context object.</param>
        public GrowthBook(Context context)
        {
            Enabled = context.Enabled;
            Attributes = context.Attributes;
            Url = context.Url;
            Features = context.Features.ToDictionary(k => k.Key, v => v.Value);
            ForcedVariations = context.ForcedVariations;
            _qaMode = context.QaMode;
            _trackingCallback = context.TrackingCallback;
            _tracked = new HashSet<string>();
            _assigned = new Dictionary<string, ExperimentAssignment>();
            _subscriptions = new List<Action<Experiment, ExperimentResult>>();
        }

        /// <summary>
        /// Arbitrary JSON object containing user and request attributes.
        /// </summary>
        public JObject Attributes { get; set; }

        /// <summary>
        /// Dictionary of the currently loaded feature objects.
        /// </summary>
        public IDictionary<string, Feature> Features { get; set; }

        /// <summary>
        /// Listing of specific experiments to always assign a specific variation (used for QA).
        /// </summary>
        public IDictionary<string, int> ForcedVariations { get; set; }

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        ///  Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Helper function used to cleanup object state.
        /// </summary>
        /// <param name="disposing">If true, dispose of large objects.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Attributes = null;
                    Features.Clear();
                    ForcedVariations = null;
                    _trackingCallback = null;
                    _tracked.Clear();
                    _assigned.Clear();
                    _subscriptions.Clear();
                }

                _disposedValue = true;
            }
        }

        /// <summary>
        /// Called to dispose of this object's data.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// GrowthBook function to dispose of object data. Alias for Dispose().
        /// </summary>
        public void Destroy()
        {
            Dispose();
        }

        /// <inheritdoc />
        public bool IsOn(string key)
        {
            return EvalFeature(key).On;
        }

        /// <inheritdoc />
        public bool IsOff(string key)
        {
            return EvalFeature(key).Off;
        }

        /// <inheritdoc />
        public T GetFeatureValue<T>(string key, T fallback)
        {
            FeatureResult result = EvalFeature(key);
            if (result.On)
            {
                return result.Value.ToObject<T>();
            }
            return fallback;
        }

        /// <inheritdoc />
        public IDictionary<string, ExperimentAssignment> GetAllResults()
        {
            return _assigned;
        }

        /// <inheritdoc />
        public Action Subscribe(Action<Experiment, ExperimentResult> callback)
        {
            _subscriptions.Add(callback);
            return () => _subscriptions.Remove(callback);
        }

        /// <inheritdoc />
        public FeatureResult EvalFeature(string key)
        {
            if (!Features.TryGetValue(key, out Feature feature))
            {
                return GetFeatureResult(null, "unknownFeature");
            }

            foreach (FeatureRule rule in feature.Rules)
            {
                if (!rule.Condition.IsNull() && !Utilities.EvalCondition(Attributes, rule.Condition))
                {
                    continue;
                }

                if (rule.Filters.Any() && IsFilteredOut(rule.Filters))
                {
                    continue;
                }

                if (!rule.Force.IsNull() && !IsIncludedInRollout(rule.Seed ?? key, rule.HashAttribute, rule.Range, rule.Coverage, rule.HashVersion))
                {
                    continue;
                }

                if (rule.Tracks.Any())
                {
                    foreach (var callback in rule.Tracks)
                    {
                        // TODO: Get current experiment/result for callback
                    }

                    return GetFeatureResult(rule.Force, "force");
                }

                var experiment = new Experiment
                {
                    Variations = rule.Variations,
                    Key = rule.Key ?? key,
                    Coverage = rule.Coverage,
                    Weights = rule.Weights,
                    HashAttribute = rule.HashAttribute,
                    Namespace = rule.Namespace,
                    Meta = rule.Meta,
                    Ranges = rule.Ranges,
                    Name = rule.Name,
                    Phase = rule.Phase,
                    Seed = rule.Seed,
                    Filters = rule.Filters
                };

                var result = Run(experiment);

                if (!result.InExperiment || result.Passthrough)
                {
                    continue;
                }

                return GetFeatureResult(result.Value, "experiment", experiment, result);
            }

            return GetFeatureResult(feature.DefaultValue ?? null, "defaultValue");
        }

        /// <inheritdoc />
        public ExperimentResult Run(Experiment experiment)
        {
            // 1. Abort if there aren't enough variations present.

            if (experiment.Variations.IsNull() || experiment.Variations.Count < 2)
            {
                return GetExperimentResult(experiment);
            }

            // 2. Abort if GrowthBook is currently disabled.

            if (!Enabled)
            {
                return GetExperimentResult(experiment);
            }

            // 3. Use the override value from the query string if one is specified.

            if (!string.IsNullOrWhiteSpace(Url))
            {
                var overrideValue = Utilities.GetQueryStringOverride(experiment.Key, Url, experiment.Variations.Count);

                if (overrideValue != null)
                {
                    return GetExperimentResult(experiment, overrideValue.Value);
                }
            }

            // 4. Use the forced value instead if one is specified.

            if (ForcedVariations.TryGetValue(experiment.Key, out var variation))
            {
                return GetExperimentResult(experiment, variation);
            }

            // 5. Abort if the experiment isn't currently active.

            if (!experiment.Active)
            {
                return GetExperimentResult(experiment);
            }

            // 6. Abort if we're unable to generate a hash identifying this run.

            var hashValue = Attributes.TryToHashWith(experiment.HashAttribute);

            if (string.IsNullOrEmpty(hashValue))
            {
                return GetExperimentResult(experiment);
            }

            // 7. Abort if this run is ineligible to be included in the experiment.

            if (experiment.Filters.Any())
            {
                if (IsFilteredOut(experiment.Filters))
                {
                    return GetExperimentResult(experiment);
                }
                else if (!Utilities.InNamespace(hashValue, experiment.Namespace))
                {
                    return GetExperimentResult(experiment);
                }
            }

            // 8. Abort if the conditions for the experiment prohibit this.

            if (!experiment.Condition.IsNull())
            {
                if (!Utilities.EvalCondition(Attributes, experiment.Condition))
                {
                    return GetExperimentResult(experiment);
                }
            }

            // 9. Attempt to assign this run to an experiment variation and abort if that can't be done.

            var ranges = experiment.Ranges ?? Utilities.GetBucketRanges(experiment.Variations.Count, experiment.Coverage ?? 1, experiment.Weights ?? Array.Empty<double>());
            var variationHash = Utilities.Hash(experiment.Seed ?? experiment.Key, hashValue, experiment.HashVersion);
            var assigned = Utilities.ChooseVariation(variationHash.Value, ranges.ToList());

            if (assigned == -1)
            {
                return GetExperimentResult(experiment);
            }

            // 10. Use the forced value for the experiment if one is specified.

            if (experiment.Force != null)
            {
                return GetExperimentResult(experiment, experiment.Force.Value);
            }

            // 11. Abort if we're currently operating in QA mode.

            if (_qaMode)
            {
                return GetExperimentResult(experiment);
            }

            // 12. Run the experiment and track the result if we haven't seen this one before.

            var result = GetExperimentResult(experiment, assigned, true);

            TryToTrack(experiment, result);

            return result;
        }

        private FeatureResult GetFeatureResult(JToken value, string source, Experiment experiment = null, ExperimentResult experimentResult = null)
        {
            return new FeatureResult
            {
                Value = value,
                Source = source,
                Experiment = experiment,
                ExperimentResult = experimentResult
            };
        }

        private bool IsFilteredOut(IEnumerable<Filter> filters)
        {
            foreach(var filter in filters)
            {
                var hashValue = Attributes.TryToHashWith(filter.Attribute);

                if (string.IsNullOrWhiteSpace(hashValue))
                {
                    return true;
                }

                var bucket = Utilities.Hash(filter.Seed, hashValue, filter.HashVersion);

                var isInAnyRange = filter.Ranges.Any(x => Utilities.InRange(bucket.Value, x));

                if (!isInAnyRange)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsIncludedInRollout(string seed, string hashAttribute = null, BucketRange range = null, double? coverage = null, int? hashVersion = null)
        {
            if (coverage == null && range == null)
            {
                return true;
            }

            var hashValue = Attributes.TryToHashWith(hashAttribute);

            if (string.IsNullOrWhiteSpace(hashValue))
            {
                return false;
            }

            var bucket = Utilities.Hash(seed, hashValue, hashVersion ?? 1);

            if (range != null)
            {
                return Utilities.InRange(bucket.Value, range);
            }

            if (coverage != null)
            {
                return bucket <= coverage;
            }

            return true;
        }

        /// <summary>
        /// Generates an experiment result from an experiment.
        /// </summary>
        /// <param name="experiment">The experiment to get the result from.</param>
        /// <param name="variationIndex">The variation id, if specified.</param>
        /// <param name="hashUsed">Whether or not a hash was used in assignment.</param>
        /// <returns>The experiment result.</returns>
        private ExperimentResult GetExperimentResult(Experiment experiment, int variationIndex = -1, bool hashUsed = false, string featureId = null, double? bucket = null)
        {
            string hashAttribute = experiment.HashAttribute ?? "id";

            bool inExperiment = true;
            if (variationIndex < 0 || variationIndex >= experiment.Variations.Count)
            {
                variationIndex = 0;
                inExperiment = false;
            }

            var hashValue = Attributes.TryToHashWith(hashAttribute);

            var meta = experiment.Meta != null ? experiment.Meta[variationIndex] : null;

            var result = new ExperimentResult
            {
                Key = meta?.Key ?? variationIndex.ToString(),
                FeatureId = featureId,
                InExperiment = inExperiment,
                HashAttribute = hashAttribute,
                HashUsed = hashUsed,
                HashValue = hashValue,
                Value = experiment.Variations[variationIndex],
                VariationId = variationIndex
            };

            result.Name = meta?.Name;
            result.Passthrough = meta?.Passthrough ?? false;
            result.Bucket = bucket ?? 0d;

            return result;
        }

        /// <summary>
        /// Calls the tracking callback function to track experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was assigned.</param>
        /// <param name="result">The result of the assignment.</param>
        private void TryToTrack(Experiment experiment, ExperimentResult result)
        {
            if (_trackingCallback == null)
            {
                return;
            }

            string key = result.HashAttribute + result.HashValue + experiment.Key + result.VariationId;
            if (!_tracked.Contains(key))
            {
                try
                {
                    _trackingCallback(experiment, result);
                    _tracked.Add(key);
                }
                catch (Exception) { }
            }
        }
    }
}
