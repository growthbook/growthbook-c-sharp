using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Providing operations to interact with feature flags.
    /// </summary>
    public interface IGrowthBook : IDisposable
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
        /// Subscribes to a GrowthBook instance to be alerted every time GrowthBook.run is called.
        /// This is different from the tracking callback since it also fires when a user is not included in an experiment.
        /// </summary>
        /// <param name="callback">The callback to trigger when GrowthBook.run is called.</param>
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

        /// <summary>
        /// Loads all available features from the API and caches them for faster retrieval.
        /// </summary>
        /// <param name="options">An optional set of choices that affect how the features will be loaded.</param>
        /// <returns>A <see cref="Task"/> that represents the feature retrieval action.</returns>
        Task LoadFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);
    }
}
