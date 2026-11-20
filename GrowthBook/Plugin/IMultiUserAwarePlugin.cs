namespace GrowthBook.Plugin
{
    /// <summary>
    /// A plugin that needs to know when it is shared by instances that may each belong to a different user.
    /// </summary>
    /// <remarks>
    /// Internal, because it describes how this SDK hands plugins around rather than an event a plugin
    /// author opts into. The tracking plugin uses it so that one user's events are not taken for another
    /// user's repeats, the way the JavaScript SDK does when a plugin is attached to a multi-user client.
    /// </remarks>
    internal interface IMultiUserAwarePlugin
    {
        /// <summary>
        /// Called before <see cref="IGrowthBookPlugin.Init"/> by every instance built from a cloned context,
        /// as <see cref="GrowthBookFactory"/> does once per user.
        /// </summary>
        void MarkShared();
    }
}
