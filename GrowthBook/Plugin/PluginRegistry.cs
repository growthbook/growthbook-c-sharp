using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Holds the plugins of one <see cref="GrowthBook"/> instance and dispatches events to them.
    /// </summary>
    /// <remarks>
    /// Which events a plugin receives is decided here, once, by type: a plugin that does not implement
    /// the matching opt-in interface is never considered again for that event. Anything a plugin throws
    /// is logged and absorbed, because a broken plugin must not change what an evaluation returns.
    /// </remarks>
    internal class PluginRegistry
    {
        private readonly IList<IGrowthBookPlugin> _plugins;
        private readonly IList<IExperimentViewedPlugin> _experimentViewed;
        private readonly IList<IFeatureEvaluatedPlugin> _featureEvaluated;
        private readonly IReadOnlyList<IGrowthBookPlugin> _sharedPlugins;
        private readonly ILogger _logger;

        public bool IsEmpty => _plugins.Count == 0;

        /// <summary>
        /// Whether anything is listening for an experiment assignment.
        /// </summary>
        public bool HasExperimentViewedListener => _experimentViewed.Count > 0;

        /// <param name="sharedPlugins">
        /// The plugins that came from the context this one was cloned from. Disposing the owning instance does
        /// not close them, because they belong to whoever built the original and other instances are still
        /// using them; a shared plugin that implements <see cref="IMultiUserAwarePlugin"/> is told so on init.
        /// Every other plugin belongs to this instance.
        /// </param>
        internal PluginRegistry(List<IGrowthBookPlugin> plugins, IReadOnlyList<IGrowthBookPlugin> sharedPlugins = null, ILogger logger = null)
        {
            _plugins = plugins ?? (IList<IGrowthBookPlugin>)Array.Empty<IGrowthBookPlugin>();
            _sharedPlugins = sharedPlugins ?? Array.Empty<IGrowthBookPlugin>();
            _logger = logger;

            _experimentViewed = _plugins.OfType<IExperimentViewedPlugin>().ToList();
            _featureEvaluated = _plugins.OfType<IFeatureEvaluatedPlugin>().ToList();
        }

        internal void InitAll()
        {
            foreach (var plugin in _plugins)
            {
                try
                {
                    if (IsShared(plugin) && plugin is IMultiUserAwarePlugin multiUserAware)
                    {
                        multiUserAware.MarkShared();
                    }

                    plugin.Init();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} Init failed; continuing as no-op", plugin.GetType().Name);
                }
            }
        }

        internal void FireExperimentViewed(Experiment experiment, ExperimentResult result, JObject attributes)
        {
            if (_experimentViewed.Count == 0)
            {
                return;
            }

            foreach (var plugin in _experimentViewed)
            {
                try
                {
                    // A copy each, so a plugin can alter neither what the caller is answered or the instance goes
                    // on evaluating against, nor what the plugins after it are told.
                    plugin.OnExperimentViewed(experiment?.Copy(), result?.Copy(), attributes?.DeepClone() as JObject);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} OnExperimentViewed failed", plugin.GetType().Name);
                }
            }
        }

        internal void FireFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes)
        {
            if (_featureEvaluated.Count == 0)
            {
                return;
            }

            foreach (var plugin in _featureEvaluated)
            {
                try
                {
                    plugin.OnFeatureEvaluated(featureKey, result?.Copy(), attributes?.DeepClone() as JObject);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} OnFeatureEvaluated failed", plugin.GetType().Name);
                }
            }
        }

        internal void CloseAll()
        {
            foreach (var plugin in _plugins)
            {
                // A shared plugin is still serving instances that are running, and closing it here would stop
                // their tracking too. Whoever made the clones closes it: the original context's instance, or
                // GrowthBookFactory.Dispose.
                if (IsShared(plugin))
                {
                    continue;
                }

                try
                {
                    plugin.Close();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} Close failed", plugin.GetType().Name);
                }
            }
        }

        private bool IsShared(IGrowthBookPlugin plugin) => _sharedPlugins.Any(shared => ReferenceEquals(shared, plugin));
    }
}
