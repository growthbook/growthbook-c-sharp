using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using GrowthBook.Plugin;
using Microsoft.Extensions.Logging.Abstractions;
using GrowthBookSdk = GrowthBook;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class PluginIntegrationTests
    {
        private class RecordingPlugin : IGrowthBookPlugin
        {
            public List<(Experiment Experiment, ExperimentResult Result)> Experiments { get; } = new List<(Experiment, ExperimentResult)>();
            public List<(string Key, FeatureResult Result)> Features { get; } = new List<(string, FeatureResult)>();
            public int InitCount;
            public int CloseCount;

            public void Init() => Interlocked.Increment(ref InitCount);
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) { lock (Experiments) Experiments.Add((e, r)); }
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) { lock (Features) Features.Add((k, r)); }
            public void Close() => Interlocked.Increment(ref CloseCount);
        }

        private class ThrowingPlugin : IGrowthBookPlugin
        {
            public void Init() { }
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) => throw new System.Exception("plugin error");
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) => throw new System.Exception("plugin error");
            public void Close() { }
        }

        private static GrowthBookSdk.GrowthBook BuildGrowthBook(List<IGrowthBookPlugin> plugins)
        {
            return new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1", ["tier"] = "gold" },
                Features = new Dictionary<string, Feature>
                {
                    ["flag-a"] = new Feature { DefaultValue = JToken.FromObject(true) },
                    ["flag-b"] = new Feature { DefaultValue = JToken.FromObject("x") },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = plugins,
            });
        }

        [Fact]
        public void PluginInitCalledOnConstruction()
        {
            var plugin = new RecordingPlugin();
            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            plugin.InitCount.Should().Be(1, "Init() must be called exactly once on construction");
        }

        [Fact]
        public void PluginObservesFeatureEvaluation()
        {
            var plugin = new RecordingPlugin();
            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            gb.EvalFeature("flag-a");
            gb.EvalFeature("flag-b");

            plugin.Features.Should().HaveCountGreaterOrEqualTo(2);
            plugin.Features.Should().Contain(f => f.Key == "flag-a");
            plugin.Features.Should().Contain(f => f.Key == "flag-b");
        }

        [Fact]
        public void PluginObservesExperimentViewed()
        {
            var plugin = new RecordingPlugin();
            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>
                {
                    ["flag-a"] = new Feature { DefaultValue = JToken.FromObject(true) },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = new List<IGrowthBookPlugin> { plugin },
                TrackingCallback = (e, r) => { },
            });

            var experiment = new Experiment
            {
                Key = "my-exp",
                Variations = JArray.Parse("[\"A\", \"B\"]"),
            };

            var result = gb.Run(experiment);

            if (result.InExperiment)
                plugin.Experiments.Should().HaveCount(1, "plugin should see experiment event exactly once");
        }

        [Fact]
        public void PluginCloseCalledOnDispose()
        {
            var plugin = new RecordingPlugin();
            var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            gb.Dispose();

            plugin.CloseCount.Should().Be(1, "Close() should fire when GrowthBook is disposed");
        }

        [Fact]
        public void MultiplePluginsEachReceiveEvents()
        {
            var first = new RecordingPlugin();
            var second = new RecordingPlugin();
            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { first, second });

            gb.EvalFeature("flag-a");

            first.Features.Should().HaveCountGreaterOrEqualTo(1);
            second.Features.Count.Should().Be(first.Features.Count, "both plugins should receive the same number of events");
        }

        [Fact]
        public void ThrowingPluginDoesNotBreakEvaluation()
        {
            var bad = new ThrowingPlugin();
            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { bad });

            var result = gb.EvalFeature("flag-a");

            result.On.Should().BeTrue("feature evaluation should succeed despite throwing plugin");
        }

        [Fact]
        public void PluginReceivesFeatureEventWithoutTrackingCallback()
        {
            var plugin = new RecordingPlugin();
            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>
                {
                    ["flag-a"] = new Feature { DefaultValue = JToken.FromObject(true) },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = new List<IGrowthBookPlugin> { plugin },
                // TrackingCallback = null навмисно
            });

            gb.EvalFeature("flag-a");

            plugin.Features.Should().Contain(f => f.Key == "flag-a",
                "plugin should observe feature evaluation even without TrackingCallback");
        }

        [Fact]
        public void AttributesArePassedToPlugin()
        {
            JObject receivedAttributes = null;
            var plugin = new CapturingAttributesPlugin(attrs => receivedAttributes = attrs);

            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });
            gb.EvalFeature("flag-a");

            receivedAttributes.Should().NotBeNull();
            receivedAttributes["id"]?.Value<string>().Should().Be("u1");
            receivedAttributes["tier"]?.Value<string>().Should().Be("gold");
        }

        private class CapturingAttributesPlugin : IGrowthBookPlugin
        {
            private readonly System.Action<JObject> _onFeature;
            public CapturingAttributesPlugin(System.Action<JObject> onFeature) => _onFeature = onFeature;
            public void Init() { }
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) { }
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) => _onFeature(a);
            public void Close() { }
        }
    }
}
