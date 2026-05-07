using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    internal class PluginRegistry
    {
        private readonly IList<IGrowthBookPlugin> _plugins;
        private readonly ILogger _logger;

        public bool IsEmpty => _plugins.Count == 0;

        internal PluginRegistry(List<IGrowthBookPlugin> plugins, ILogger logger = null)
        {
            _plugins = plugins ?? new List<IGrowthBookPlugin>();
            _logger = logger;
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
            if (_plugins.Count == 0) return;
            foreach (var plugin in _plugins)
            {
                try
                {
                    plugin.OnExperimentViewed(experiment, result, attributes);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Plugin {Plugin} OnExperimentViewed failed", plugin.GetType().Name);
                }
            }
        }

        internal void FireFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes)
        {
            if (_plugins.Count == 0) return;
            foreach (var plugin in _plugins)
            {
                try
                {
                    plugin.OnFeatureEvaluated(featureKey, result, attributes);
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
