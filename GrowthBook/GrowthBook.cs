using System;
using System.Collections.Generic;
using System.Linq;
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
        // #region Private Members

        readonly bool _qaMode;
        readonly Dictionary<string, ExperimentAssignment> _assigned;
        readonly HashSet<string> _tracked;
        Action<Experiment, ExperimentResult> _trackingCallback;
        readonly List<Action<Experiment, ExperimentResult>> _subscriptions;
        bool _disposedValue;

        // #endregion

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

        // #region Properties

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
        public JObject ForcedVariations { get; set; }

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        ///  Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled { get; set; }

        // #endregion

        // #region Cleanup

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

        // #endregion

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
                return new FeatureResult { Source = "unknownFeature" };
            }

            foreach (FeatureRule rule in feature.Rules)
            {
                if (rule.Condition != null && rule.Condition.Type != JTokenType.Null && !Utilities.EvalCondition(Attributes, rule.Condition))
                {
                    continue;
                }

                if (rule.Force != null && rule.Force.Type != JTokenType.Null)
                {
                    if (rule.Coverage < 1)
                    {
                        string hashValue = GetHashValue(rule.HashAttribute);
                        if (string.IsNullOrEmpty(hashValue))
                        {
                            continue;
                        }

                        double n = Utilities.Hash(hashValue + key);
                        if (n > rule.Coverage)
                        {
                            continue;
                        }
                    }

                    return new FeatureResult { Value = rule.Force, Source = "force" };
                }

                if (rule.Variations == null || rule.Variations.Type == JTokenType.Null)
                {
                    continue;
                }

                Experiment exp = new Experiment
                {
                    Key = rule.Key ?? key,
                    Variations = rule.Variations,
                    Coverage = rule.Coverage,
                    Weights = rule.Weights,
                    HashAttribute = rule.HashAttribute,
                    Namespace = rule.Namespace
                };

                ExperimentResult result = Run(exp);
                if (!result.InExperiment)
                {
                    continue;
                }

                return new FeatureResult { Value = result.Value, Source = "experiment", Experiment = exp, ExperimentResult = result };
            }

            return new FeatureResult { Value = feature.DefaultValue, Source = "defaultValue" };
        }

        /// <inheritdoc />
        public ExperimentResult Run(Experiment experiment)
        {
            ExperimentResult result = RunExperiment(experiment);

            if (!_assigned.TryGetValue(experiment.Key, out ExperimentAssignment prev)
                || prev.Result.InExperiment != result.InExperiment
                || prev.Result.VariationId != result.VariationId)
            {
                _assigned.Add(experiment.Key, new ExperimentAssignment { Experiment = experiment, Result = result });
                foreach (Action<Experiment, ExperimentResult> cb in _subscriptions)
                {
                    try
                    {
                        cb.Invoke(experiment, result);
                    }
                    catch (Exception) { }
                }
            }

            return result;
        }

        // #region Private Helper Methods

        /// <summary>
        /// Evaluates an experiment to generate the result.
        /// </summary>
        /// <param name="experiment">The experiment to evaluate.</param>
        /// <returns>The experiment result.</returns>
        ExperimentResult RunExperiment(Experiment experiment)
        {
            // 1. If experiment has less than 2 variations, return immediately
            if (experiment.Variations.Count < 2)
            {
                return GetExperimentResult(experiment);
            }

            // 2. If growthbook is disabled, return immediately
            if (!Enabled)
            {
                return GetExperimentResult(experiment);
            }

            // 3. If experiment is forced via a querystring in the url
            int? queryString = Utilities.GetQueryStringOverride(experiment.Key, Url, experiment.Variations.Count);
            if (queryString != null)
            {
                return GetExperimentResult(experiment, (int)queryString);
            }

            // 4. If variation is forced in the context
            if (ForcedVariations.TryGetValue(experiment.Key, out JToken forcedVariation))
            {
                return GetExperimentResult(experiment, forcedVariation.ToObject<int>());
            }

            // 5. If experiment is a draft or not active, return immediately
            if (!experiment.Active)
            {
                return GetExperimentResult(experiment);
            }

            // 6. Get the user hash attribute and value
            string hashAttribute = experiment.HashAttribute ?? "id";
            string hashValue = GetHashValue(hashAttribute);
            if (string.IsNullOrEmpty(hashValue))
            {
                return GetExperimentResult(experiment);
            }

            // 7. Exclude if user not in experiment.namespace
            if (experiment.Namespace != null && !Utilities.InNamespace(hashValue, experiment.Namespace))
            {
                return GetExperimentResult(experiment);
            }

            // 8. Exclude if condition is false
            if (experiment.Condition != null && experiment.Condition.Type != JTokenType.Null && !Utilities.EvalCondition(Attributes, experiment.Condition))
            {
                return GetExperimentResult(experiment);
            }

            // 9. Get bucket ranges and choose variation
            IList<BucketRange> ranges = Utilities.GetBucketRanges(experiment.Variations.Count, experiment.Coverage, experiment.Weights);
            double n = Utilities.Hash(hashValue + experiment.Key);
            int assigned = Utilities.ChooseVariation(n, ranges);

            // 10. Return if not in experiment
            if (assigned < 0)
            {
                return GetExperimentResult(experiment);
            }

            // 11. If experiment is forced, return immediately
            if (experiment.Force != null)
            {
                return GetExperimentResult(experiment, (int)experiment.Force);
            }

            // 12. Exclude if in QA mode
            if (_qaMode)
            {
                return GetExperimentResult(experiment);
            }

            // 13. Build the result object
            ExperimentResult result = GetExperimentResult(experiment, assigned, true);

            // 14. Fire the tracking callback if set
            if (_trackingCallback != null)
            {
                Track(experiment, result);
            }

            // 15. Return the result
            return result;
        }

        /// <summary>
        /// Generates an experiment result from an experiment.
        /// </summary>
        /// <param name="experiment">The experiment to get the result from.</param>
        /// <param name="variationId">The variation id, if specified.</param>
        /// <param name="hashUsed">Whether or not a hash was used in assignment.</param>
        /// <returns>The experiment result.</returns>
        ExperimentResult GetExperimentResult(Experiment experiment, int variationId = -1, bool hashUsed = false)
        {
            string hashAttribute = experiment.HashAttribute ?? "id";

            bool inExperiment = true;
            if (variationId < 0 || variationId > experiment.Variations.Count - 1)
            {
                variationId = 0;
                inExperiment = false;
            }

            return new ExperimentResult
            {
                InExperiment = inExperiment,
                HashAttribute = hashAttribute,
                HashUsed = hashUsed,
                HashValue = GetHashValue(hashAttribute),
                Value = experiment.Variations[variationId],
                VariationId = variationId
            };
        }

        /// <summary>
        /// Gets the attribute value for the specified key.
        /// </summary>
        /// <param name="attr">The attribute key.</param>
        /// <returns>The attribute value.</returns>
        string GetHashValue(string attr)
        {
            if (Attributes.ContainsKey(attr))
            {
                return Attributes[attr].ToString();
            }
            return string.Empty;
        }

        /// <summary>
        /// Calls the tracking callback function to track experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was assigned.</param>
        /// <param name="result">The result of the assignment.</param>
        void Track(Experiment experiment, ExperimentResult result)
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

        // #endregion
    }
}
