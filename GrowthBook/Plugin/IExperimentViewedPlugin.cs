using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Implemented by an <see cref="IGrowthBookPlugin"/> that wants experiment assignments.
    /// </summary>
    public interface IExperimentViewedPlugin
    {
        /// <summary>
        /// Called when a user is assigned to a variation, once per distinct assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated.</param>
        /// <param name="result">The assignment that was made.</param>
        /// <param name="attributes">
        /// A copy of the attributes evaluation used. Mutating it does not affect the instance.
        /// </param>
        void OnExperimentViewed(Experiment experiment, ExperimentResult result, JObject attributes);
    }
}
