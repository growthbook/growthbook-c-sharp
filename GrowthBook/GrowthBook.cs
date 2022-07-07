using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GrowthBook {
    /// <summary>
    /// This is the C# client library for GrowthBook, the open-source
    //  feature flagging and A/B testing platform.
    //  More info at https://www.growthbook.io
    /// </summary>
    public class GrowthBook : IDisposable {
        // #region Private Members

        bool enabled;
        JObject attributes;
        string url;
        IDictionary<string, Feature> features;
        JObject forcedVariations;
        bool qaMode;
        Action<Experiment, ExperimentResult> trackingCallback;
        Dictionary<string, ExperimentAssignment> assigned;
        HashSet<string> tracked;
        List<Action<Experiment, ExperimentResult>> subscriptions;
        bool disposedValue;

        // #endregion

        /// <summary>
        /// Creates a new GrowthBook instance from the passed context.
        /// </summary>
        /// <param name="context">The GrowthBook Context object.</param>
        public GrowthBook(Context context) {
            enabled = context.Enabled;
            attributes = context.Attributes;
            url = context.Url;
            features = context.Features;
            forcedVariations = context.ForcedVariations;
            qaMode = context.QaMode;
            trackingCallback = context.TrackingCallback;
            tracked = new HashSet<string>();
            assigned = new Dictionary<string, ExperimentAssignment>();
            subscriptions = new List<Action<Experiment, ExperimentResult>>();
        }

        // #region Properties

        /// <summary>
        /// Arbitrary JSON object containing user and request attributes.
        /// </summary>
        public JObject Attributes {
            get { return attributes; }
            set { attributes = value; }
        }

        /// <summary>
        /// Dictionary of the currently loaded feature objects.
        /// </summary>
        public IDictionary<string, Feature> Features {
            get { return features; }
            set { features = value; }
        }

        /// <summary>
        /// Listing of specific experiments to always assign a specific variation (used for QA).
        /// </summary>
        public JObject ForcedVariations {
            get { return forcedVariations; }
            set { forcedVariations = value; }
        }

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url {
            get { return url; }
            set { url = value; }
        }

        /// <summary>
        ///  Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled {
            get { return enabled; }
            set { enabled = value; }
        }

        // #endregion

        // #region Cleanup

        /// <summary>
        /// Helper function used to cleanup object state.
        /// </summary>
        /// <param name="disposing">If true, dispose of large objects.</param>
        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    attributes = null;
                    features.Clear();
                    forcedVariations = null;
                    trackingCallback = null;
                    tracked.Clear();
                    assigned.Clear();
                    subscriptions.Clear();
                }

                disposedValue = true;
            }
        }

        /// <summary>
        /// Called to dispose of this object's data.
        /// </summary>
        public void Dispose() {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// GrowthBook function to dispose of object data. Alias for Dispose().
        /// </summary>
        public void Destroy() {
            Dispose();
        }

        // #endregion

        /// <summary>
        /// Checks to see if a feature is on.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is on.</returns>
        public bool IsOn(string key) {
            return EvalFeature(key).On;
        }

        /// <summary>
        /// Checks to see if a feature is off.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is off.</returns>
        public bool IsOff(string key) {
            return EvalFeature(key).Off;
        }

        /// <summary>
        /// Gets the value of a feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The feature key.</param>
        /// <param name="fallback">Fallback value to return if the feature is not on.</param>
        /// <returns>Value of a feature cast to the specified type.</returns>
        public T GetFeatureValue<T>(string key, T fallback) {
            FeatureResult result = EvalFeature(key);
            if (result.On) {
                return result.Value.ToObject<T>();
            }
            return fallback;
        }

        /// <summary>
        /// Returns a map of the latest results indexed by experiment key.
        /// </summary>
        /// <returns></returns>
        public IDictionary<string, ExperimentAssignment> GetAllResults() {
            return assigned;
        }

        /// <summary>
        /// Subscribes to a GrowthBook instance to be alerted every time growthbook.run is called.
        /// This is different from the tracking callback since it also fires when a user is not included in an experiment.
        /// </summary>
        /// <param name="callback">The callback to trigger when growthbook.run is called.</param>
        /// <returns>An action callback that can be used to unsubscribe.</returns>
        public Action Subscribe(Action<Experiment, ExperimentResult> callback) {
            subscriptions.Add(callback);
            return () => subscriptions.Remove(callback);
        }

        /// <summary>
        /// Evaluates a feature and returns a feature result.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>The feature result.</returns>
        public FeatureResult EvalFeature(string key) {
            Feature feature;
            if (!features.TryGetValue(key, out feature)) {
                return new FeatureResult { Source = "unknownFeature" };
            }

            foreach (FeatureRule rule in feature.Rules) {
                if (rule.Condition != null && rule.Condition.Type != JTokenType.Null && !Utilities.EvalCondition(attributes, rule.Condition)) {
                    continue;
                }

                if (rule.Force != null && rule.Force.Type != JTokenType.Null) {
                    if (rule.Coverage < 1) {
                        string hashValue = GetHashValue(rule.HashAttribute);
                        if (string.IsNullOrEmpty(hashValue)) {
                            continue;
                        }

                        double n = Utilities.Hash(hashValue + key);
                        if (n > rule.Coverage) {
                            continue;
                        }
                    }

                    return new FeatureResult { Value = rule.Force, Source = "force" };
                }

                if (rule.Variations == null || rule.Variations.Type == JTokenType.Null) {
                    continue;
                }

                Experiment exp = new Experiment {
                    Key = rule.Key ?? key,
                    Variations = rule.Variations,
                    Coverage = rule.Coverage,
                    Weights = rule.Weights,
                    HashAttribute = rule.HashAttribute,
                    Namespace = rule.Namespace
                };

                ExperimentResult result = Run(exp);
                if (!result.InExperiment) {
                    continue;
                }

                return new FeatureResult { Value = result.Value, Source = "experiment", Experiment = exp, ExperimentResult = result };
            }

            return new FeatureResult { Value = feature.DefaultValue, Source = "defaultValue" };
        }

        /// <summary>
        /// Evaluates an experiment and returns an experiment result.
        /// </summary>
        /// <param name="experiment">The experiment to evaluate.</param>
        /// <returns>The experiment result.</returns>
        public ExperimentResult Run(Experiment experiment) {
            ExperimentResult result = RunExperiment(experiment);

            ExperimentAssignment prev;
            if (!assigned.TryGetValue(experiment.Key, out prev)
                || prev.Result.InExperiment != result.InExperiment
                || prev.Result.VariationId != result.VariationId) {
                assigned.Add(experiment.Key, new ExperimentAssignment { Experiment = experiment, Result = result });
                foreach (Action<Experiment, ExperimentResult> cb in subscriptions) {
                    try {
                        cb.Invoke(experiment, result);
                    } catch (Exception) { }
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
        ExperimentResult RunExperiment(Experiment experiment) {
            // 1. If experiment has less than 2 variations, return immediately
            if (experiment.Variations.Count < 2) {
                return GetExperimentResult(experiment);
            }

            // 2. If growthbook is disabled, return immediately
            if (!enabled) {
                return GetExperimentResult(experiment);
            }

            // 3. If experiment is forced via a querystring in the url
            int? queryString = Utilities.GetQueryStringOverride(experiment.Key, url, experiment.Variations.Count);
            if (queryString != null) {
                return GetExperimentResult(experiment, (int)queryString);
            }

            // 4. If variation is forced in the context
            JToken forcedVariation;
            if (forcedVariations.TryGetValue(experiment.Key, out forcedVariation)) {
                return GetExperimentResult(experiment, forcedVariation.ToObject<int>());
            }

            // 5. If experiment is a draft or not active, return immediately
            if (!experiment.Active) {
                return GetExperimentResult(experiment);
            }

            // 6. Get the user hash attribute and value
            string hashAttribute = experiment.HashAttribute ?? "id";
            string hashValue = GetHashValue(hashAttribute);
            if (string.IsNullOrEmpty(hashValue)) {
                return GetExperimentResult(experiment);
            }

            // 7. Exclude if user not in experiment.namespace
            if (experiment.Namespace != null && !Utilities.InNamespace(hashValue, experiment.Namespace)) {
                return GetExperimentResult(experiment);
            }

            // 8. Exclude if condition is false
            if (experiment.Condition != null && experiment.Condition.Type != JTokenType.Null && !Utilities.EvalCondition(attributes, experiment.Condition)) {
                return GetExperimentResult(experiment);
            }

            // 9. Get bucket ranges and choose variation
            IList<BucketRange> ranges = Utilities.GetBucketRanges(experiment.Variations.Count, experiment.Coverage, experiment.Weights);
            double n = Utilities.Hash(hashValue + experiment.Key);
            int assigned = Utilities.ChooseVariation(n, ranges);

            // 10. Return if not in experiment
            if (assigned < 0) {
                return GetExperimentResult(experiment);
            }

            // 11. If experiment is forced, return immediately
            if (experiment.Force != null) {
                return GetExperimentResult(experiment, (int)experiment.Force);
            }

            // 12. Exclude if in QA mode
            if (qaMode) {
                return GetExperimentResult(experiment);
            }

            // 13. Build the result object
            ExperimentResult result = GetExperimentResult(experiment, assigned, true);

            // 14. Fire the tracking callback if set
            if (trackingCallback != null) {
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
        ExperimentResult GetExperimentResult(Experiment experiment, int variationId = -1, bool hashUsed = false) {
            string hashAttribute = experiment.HashAttribute ?? "id";

            bool inExperiment = true;
            if (variationId < 0 || variationId > experiment.Variations.Count - 1) {
                variationId = 0;
                inExperiment = false;
            }

            return new ExperimentResult {
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
        string GetHashValue(string attr) {
            if (attributes.ContainsKey(attr)) {
                return attributes[attr].ToString();
            }
            return string.Empty;
        }

        /// <summary>
        /// Calls the tracking callback function to track experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was assigned.</param>
        /// <param name="result">The result of the assignment.</param>
        void Track(Experiment experiment, ExperimentResult result) {
            if (trackingCallback == null) {
                return;
            }

            string key = result.HashAttribute + result.HashValue + experiment.Key + result.VariationId;
            if (!tracked.Contains(key)) {
                try {
                    trackingCallback(experiment, result);
                    tracked.Add(key);
                } catch (Exception) { }
            }
        }

        // #endregion
    }
}