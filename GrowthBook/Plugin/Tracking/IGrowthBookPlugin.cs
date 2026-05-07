using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Defines a plugin that can observe GrowthBook experiment and feature evaluation events.
    /// Implement this interface to receive lifecycle callbacks from a <see cref="GrowthBook"/> instance.
    /// </summary>
    public interface IGrowthBookPlugin
    {
        /// <summary>
        /// Called once when the GrowthBook instance is constructed.
        /// Use this to perform any one-time setup required by the plugin.
        /// </summary>
        void Init();

        /// <summary>
        /// Called each time a user is assigned to an experiment variation.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated.</param>
        /// <param name="result">The result of the experiment assignment.</param>
        /// <param name="attributes">The current user attributes at the time of evaluation.</param>
        void OnExperimentViewed(Experiment experiment, ExperimentResult result, JObject attributes);

        /// <summary>
        /// Called each time a feature flag is evaluated.
        /// </summary>
        /// <param name="featureKey">The key of the evaluated feature.</param>
        /// <param name="result">The result of the feature evaluation.</param>
        /// <param name="attributes">The current user attributes at the time of evaluation.</param>
        void OnFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes);

        /// <summary>
        /// Called when the GrowthBook instance is disposed.
        /// Use this to flush any pending data and release resources.
        /// </summary>
        void Close();
    }
}
