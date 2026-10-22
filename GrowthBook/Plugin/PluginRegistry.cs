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
        private readonly bool _ownsPlugins;
        private readonly ILogger _logger;

        public bool IsEmpty => _plugins.Count == 0;

        /// <summary>
        /// Whether anything is listening for an experiment assignment.
        /// </summary>
        public bool HasExperimentViewedListener => _experimentViewed.Count > 0;

        /// <param name="ownsPlugins">
        /// Whether disposing the owning instance should close these plugins. False when the context was
        /// cloned, because the plugins belong to whoever built the original and other instances are still
        /// using them.
        /// </param>
        internal PluginRegistry(List<IGrowthBookPlugin> plugins, bool ownsPlugins = true, ILogger logger = null)
        {
            _plugins = plugins ?? (IList<IGrowthBookPlugin>)Array.Empty<IGrowthBookPlugin>();
            _ownsPlugins = ownsPlugins;
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

            // A copy, so a plugin cannot alter the attributes the instance goes on evaluating against.
            var snapshot = attributes?.DeepClone() as JObject;

            foreach (var plugin in _experimentViewed)
            {
                try
                {
                    plugin.OnExperimentViewed(experiment, result, snapshot);
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

            var snapshot = attributes?.DeepClone() as JObject;

            foreach (var plugin in _featureEvaluated)
            {
                try
                {
                    plugin.OnFeatureEvaluated(featureKey, result, snapshot);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} OnFeatureEvaluated failed", plugin.GetType().Name);
                }
            }
        }

        internal void CloseAll()
        {
            if (!_ownsPlugins)
            {
                // The context was cloned, so these plugins are shared with instances that are still
                // running. Closing them here would stop their tracking too.
                return;
            }

            foreach (var plugin in _plugins)
            {
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
    }
}
