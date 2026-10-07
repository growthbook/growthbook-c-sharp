using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using GrowthBook.Plugin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class PluginRegistryTests
    {
        private static Experiment MakeExperiment() => new Experiment { Key = "e" };
        private static ExperimentResult MakeExperimentResult() => new ExperimentResult();
        private static FeatureResult MakeFeatureResult() => new FeatureResult { Source = FeatureResult.SourceId.DefaultValue };

        private class CountingPlugin : IGrowthBookPlugin
        {
            public int InitCount;
            public int ExperimentCount;
            public int FeatureCount;
            public int CloseCount;

            public void Init() => Interlocked.Increment(ref InitCount);
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) => Interlocked.Increment(ref ExperimentCount);
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) => Interlocked.Increment(ref FeatureCount);
            public void Close() => Interlocked.Increment(ref CloseCount);
        }

        private class ThrowingPlugin : IGrowthBookPlugin
        {
            public void Init() => throw new Exception("boom");
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) => throw new Exception("boom");
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) => throw new Exception("boom");
            public void Close() => throw new Exception("boom");
        }

        [Fact]
        public void DispatchesToEachPlugin()
        {
            var p1 = new CountingPlugin();
            var p2 = new CountingPlugin();
            var registry = new PluginRegistry(new List<IGrowthBookPlugin> { p1, p2 });

            registry.InitAll();
            registry.FireExperimentViewed(MakeExperiment(), MakeExperimentResult(), null);
            registry.FireFeatureEvaluated("f", MakeFeatureResult(), null);
            registry.CloseAll();

            p1.InitCount.Should().Be(1);
            p1.ExperimentCount.Should().Be(1);
            p1.FeatureCount.Should().Be(1);
            p1.CloseCount.Should().Be(1);

            p2.InitCount.Should().Be(1);
            p2.ExperimentCount.Should().Be(1);
            p2.FeatureCount.Should().Be(1);
            p2.CloseCount.Should().Be(1);
        }

        [Fact]
        public void OnePluginThrowingDoesNotStopOthers()
        {
            var good = new CountingPlugin();
            var registry = new PluginRegistry(new List<IGrowthBookPlugin> { new ThrowingPlugin(), good });

            registry.InitAll();
            registry.FireExperimentViewed(MakeExperiment(), MakeExperimentResult(), null);
            registry.FireFeatureEvaluated("f", MakeFeatureResult(), null);
            registry.CloseAll();

            good.InitCount.Should().Be(1);
            good.ExperimentCount.Should().Be(1);
            good.FeatureCount.Should().Be(1);
            good.CloseCount.Should().Be(1, "good plugin should receive all 4 lifecycle events");
        }

        [Fact]
        public void EmptyRegistryIsNoOp()
        {
            var registry = new PluginRegistry(new List<IGrowthBookPlugin>());

            var act = () =>
            {
                registry.InitAll();
                registry.FireExperimentViewed(MakeExperiment(), MakeExperimentResult(), null);
                registry.FireFeatureEvaluated("f", MakeFeatureResult(), null);
                registry.CloseAll();
            };

            act.Should().NotThrow();
        }

        [Fact]
        public void NullPluginListIsNoOp()
        {
            var registry = new PluginRegistry(null);

            var act = () =>
            {
                registry.InitAll();
                registry.CloseAll();
            };

            act.Should().NotThrow();
        }

        [Fact]
        public void IsEmptyReturnsTrueForEmptyList()
        {
            var registry = new PluginRegistry(new List<IGrowthBookPlugin>());
            registry.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void IsEmptyReturnsFalseWhenPluginsExist()
        {
            var registry = new PluginRegistry(new List<IGrowthBookPlugin> { new CountingPlugin() });
            registry.IsEmpty.Should().BeFalse();
        }
    }
}
