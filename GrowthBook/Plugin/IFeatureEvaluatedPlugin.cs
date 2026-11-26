using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Implemented by an <see cref="IGrowthBookPlugin"/> that wants feature evaluations.
    /// </summary>
    public interface IFeatureEvaluatedPlugin
    {
        /// <summary>
        /// Called once for each feature evaluation a caller asks for, whichever public method they used.
        /// </summary>
        /// <remarks>
        /// Prerequisite features evaluated on the way to an answer do not raise this: the caller asked
        /// about one feature, so one event describes what they asked.
        /// </remarks>
        /// <param name="featureKey">The key that was evaluated.</param>
        /// <param name="result">The result the caller received.</param>
        /// <param name="attributes">
        /// A copy of the attributes evaluation used. Mutating it does not affect the instance.
        /// </param>
        void OnFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes);
    }
}
