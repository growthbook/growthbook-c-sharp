using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Plugin;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using GrowthBookSdk = GrowthBook;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class PluginIntegrationTests
    {
        private class RecordingPlugin : IGrowthBookPlugin, IExperimentViewedPlugin, IFeatureEvaluatedPlugin
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

        private class ThrowingPlugin : IGrowthBookPlugin, IExperimentViewedPlugin, IFeatureEvaluatedPlugin
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

        private class CapturingAttributesPlugin : IGrowthBookPlugin, IExperimentViewedPlugin, IFeatureEvaluatedPlugin
        {
            private readonly System.Action<JObject> _onFeature;
            public CapturingAttributesPlugin(System.Action<JObject> onFeature) => _onFeature = onFeature;
            public void Init() { }
            public void OnExperimentViewed(Experiment e, ExperimentResult r, JObject a) { }
            public void OnFeatureEvaluated(string k, FeatureResult r, JObject a) => _onFeature(a);
            public void Close() { }
        }

        /// <summary>
        /// What a plugin is told must not depend on which of the public ways of checking a flag the
        /// caller happened to use.
        /// </summary>
        [Theory]
        [InlineData("EvalFeature")]
        [InlineData("GetFeatureValue")]
        [InlineData("IsOn")]
        [InlineData("IsOff")]
        [InlineData("IsOnAsync")]
        [InlineData("IsOffAsync")]
        [InlineData("GetFeatureValueAsync")]
        [InlineData("EvalFeatureAsync")]
        public async Task EveryPublicFeatureCheckIsReportedExactlyOnce(string method)
        {
            var plugin = new RecordingPlugin();
            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            switch (method)
            {
                case "EvalFeature": gb.EvalFeature("flag-a"); break;
                case "GetFeatureValue": gb.GetFeatureValue("flag-a", false); break;
                case "IsOn": gb.IsOn("flag-a"); break;
                case "IsOff": gb.IsOff("flag-a"); break;
                case "IsOnAsync": await gb.IsOnAsync("flag-a"); break;
                case "IsOffAsync": await gb.IsOffAsync("flag-a"); break;
                case "GetFeatureValueAsync": await gb.GetFeatureValueAsync("flag-a", false); break;
                case "EvalFeatureAsync": await gb.EvalFeatureAsync("flag-a"); break;
            }

            plugin.Features.Should().HaveCount(1, $"{method} evaluates a feature, so it reports one")
                .And.OnlyContain(f => f.Key == "flag-a");
        }

        /// <summary>
        /// A prerequisite is evaluated on the way to an answer, not asked about. Reporting it would
        /// describe work the caller never requested, and would double-count the flags that use one.
        /// </summary>
        [Fact]
        public void APrerequisiteIsNotReportedAsItsOwnEvaluation()
        {
            var plugin = new RecordingPlugin();

            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>
                {
                    ["parent"] = new Feature { DefaultValue = JToken.FromObject(true) },
                    ["child"] = new Feature
                    {
                        DefaultValue = JToken.FromObject(false),
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                ParentConditions = new List<ParentCondition>
                                {
                                    new ParentCondition { Id = "parent", Condition = JObject.Parse("{\"value\":true}") }
                                },
                                Force = JToken.FromObject(true),
                            }
                        }
                    },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = new List<IGrowthBookPlugin> { plugin },
            });

            gb.EvalFeature("child");

            plugin.Features.Should().HaveCount(1, "one feature was asked about")
                .And.OnlyContain(f => f.Key == "child");
        }

        /// <summary>
        /// A context is cloned once per user by <see cref="GrowthBookFactory"/>, and the clone carries
        /// the same plugin objects. Disposing one user must not close tracking for the others.
        /// </summary>
        [Fact]
        public void DisposingOneUserDoesNotCloseAPluginTheOthersShare()
        {
            var plugin = new RecordingPlugin();

            var shared = new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>
                {
                    ["flag-a"] = new Feature { DefaultValue = JToken.FromObject(true) },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = new List<IGrowthBookPlugin> { plugin },
            };

            var first = new GrowthBookSdk.GrowthBook(shared.Clone());
            using var second = new GrowthBookSdk.GrowthBook(shared.Clone());

            first.Dispose();

            plugin.CloseCount.Should().Be(0, "the plugin belongs to the context that was cloned, not to one user");

            second.EvalFeature("flag-a");

            plugin.Features.Should().ContainSingle("and the users still running keep being tracked");
        }

        /// <summary>
        /// The owner of the context still closes its plugins, otherwise nothing would ever flush them.
        /// </summary>
        [Fact]
        public void TheInstanceBuiltFromTheOriginalContextStillClosesItsPlugins()
        {
            var plugin = new RecordingPlugin();
            var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            gb.Dispose();

            plugin.CloseCount.Should().Be(1);
        }

        /// <summary>
        /// A consumer's callback throwing is their problem; the plugins were going to be told about the
        /// same assignment and still must be.
        /// </summary>
        [Fact]
        public void AThrowingTrackingCallbackDoesNotHideARuleTrackFromPlugins()
        {
            var plugin = new RecordingPlugin();

            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                TrackingCallback = (e, r) => throw new System.InvalidOperationException("the consumer's callback is broken"),
                Features = new Dictionary<string, Feature>
                {
                    ["flag-a"] = new Feature
                    {
                        DefaultValue = JToken.FromObject(false),
                        Rules = new List<FeatureRule>
                        {
                            new FeatureRule
                            {
                                Force = JToken.FromObject(true),
                                Tracks = new List<TrackData>
                                {
                                    new TrackData
                                    {
                                        Experiment = new Experiment { Key = "exp-1" },
                                        Result = new ExperimentResult { VariationId = 1, HashAttribute = "id", HashValue = "u1" }
                                    }
                                }
                            }
                        }
                    },
                },
                LoggerFactory = NullLoggerFactory.Instance,
                Plugins = new List<IGrowthBookPlugin> { plugin },
            });

            gb.EvalFeature("flag-a");

            plugin.Experiments.Should().ContainSingle(e => e.Experiment.Key == "exp-1",
                "the plugin event is dispatched whatever the legacy callback did");
        }

        /// <summary>
        /// The attributes handed to a plugin are a copy. A plugin is a bystander, and one that writes to
        /// what it was given would change the answers every later evaluation gives.
        /// </summary>
        [Fact]
        public void APluginCannotChangeTheAttributesEvaluationUses()
        {
            var plugin = new CapturingAttributesPlugin(attrs => attrs["tier"] = "platinum");

            using var gb = BuildGrowthBook(new List<IGrowthBookPlugin> { plugin });

            gb.EvalFeature("flag-a");

            gb.Attributes["tier"]?.Value<string>().Should().Be("gold",
                "the plugin was given a copy, so writing to it changed nothing here");
        }

        /// <summary>
        /// With nothing to report an assignment to, it must not be recorded as reported. The mark is
        /// shared through the feature repository, so a later instance that does have a callback would
        /// find the key already there and stay silent about an assignment nobody ever heard.
        /// </summary>
        [Fact]
        public void AnAssignmentNobodyIsListeningForIsNotMarkedAsTracked()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();

            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>(),
                LoggerFactory = NullLoggerFactory.Instance,
                FeatureRepository = repository,
                // No TrackingCallback and no plugins: nothing would hear about this assignment.
            });

            gb.Run(new Experiment { Key = "exp-1", Variations = new JArray(0, 1) });

            repository.DidNotReceive().TryMarkAsTracked(Arg.Any<string>());
        }

        [Fact]
        public void AnAssignmentAPluginIsListeningForIsMarkedAsTracked()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            repository.TryMarkAsTracked(Arg.Any<string>()).Returns(true);

            using var gb = new GrowthBookSdk.GrowthBook(new Context
            {
                Enabled = true,
                Attributes = new JObject { ["id"] = "u1" },
                Features = new Dictionary<string, Feature>(),
                LoggerFactory = NullLoggerFactory.Instance,
                FeatureRepository = repository,
                Plugins = new List<IGrowthBookPlugin> { new RecordingPlugin() },
            });

            gb.Run(new Experiment { Key = "exp-1", Variations = new JArray(0, 1) });

            repository.Received().TryMarkAsTracked(Arg.Any<string>());
        }
    }
}
