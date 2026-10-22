namespace GrowthBook.Plugin
{
    /// <summary>
    /// A plugin attached to a <see cref="GrowthBook"/> instance through <see cref="Context.Plugins"/>.
    /// </summary>
    /// <remarks>
    /// Only the lifetime is required here. The events a plugin wants are opted into by also implementing
    /// <see cref="IExperimentViewedPlugin"/> or <see cref="IFeatureEvaluatedPlugin"/>, which the SDK
    /// discovers by type.
    /// <para>
    /// Split that way because this library targets netstandard2.0, which has no default interface
    /// members: every event declared here would be a member each existing plugin has to implement, so a
    /// hook added later could not be added without breaking them. An opt-in interface costs a plugin that
    /// does not want the event nothing at all.
    /// </para>
    /// </remarks>
    public interface IGrowthBookPlugin
    {
        /// <summary>
        /// Called once while the instance is being constructed, before any evaluation can happen.
        /// </summary>
        /// <remarks>
        /// Anything thrown here is logged and swallowed: a broken plugin must not stop the SDK starting.
        /// The plugin stays registered, so it will still receive events - guard your own state.
        /// </remarks>
        void Init();

        /// <summary>
        /// Called once when the instance that owns this plugin is disposed.
        /// </summary>
        /// <remarks>
        /// The place to flush buffered data and release resources. A plugin reached through a cloned
        /// context is shared with other instances and is not closed by them; see <see cref="Context.Clone"/>.
        /// </remarks>
        void Close();
    }
}
