using System;
using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// Providing operations to interact with feature flags.
    /// </summary>
    public interface IGrowthbook
    {
        /// <summary>
        /// Checks to see if a feature is on.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is on.</returns>
        bool IsOn(string key);

        /// <summary>
        /// Checks to see if a feature is off.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is off.</returns>
        bool IsOff(string key);

        /// <summary>
        /// Gets the value of a feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The feature key.</param>
        /// <param name="fallback">Fallback value to return if the feature is not on.</param>
        /// <returns>Value of a feature cast to the specified type.</returns>
        T GetFeatureValue<T>(string key, T fallback);

        /// <summary>
        /// Returns a map of the latest results indexed by experiment key.
        /// </summary>
        /// <returns></returns>
        IDictionary<string, ExperimentAssignment> GetAllResults();

        /// <summary>
        /// Subscribes to a GrowthBook instance to be alerted every time growthbook.run is called.
        /// This is different from the tracking callback since it also fires when a user is not included in an experiment.
        /// </summary>
        /// <param name="callback">The callback to trigger when growthbook.run is called.</param>
        /// <returns>An action callback that can be used to unsubscribe.</returns>
        Action Subscribe(Action<Experiment, ExperimentResult> callback);

        /// <summary>
        /// Evaluates a feature and returns a feature result.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>The feature result.</returns>
        FeatureResult EvalFeature(string key);

        /// <summary>
        /// Evaluates an experiment and returns an experiment result.
        /// </summary>
        /// <param name="experiment">The experiment to evaluate.</param>
        /// <returns>The experiment result.</returns>
        ExperimentResult Run(Experiment experiment);
    }
}
