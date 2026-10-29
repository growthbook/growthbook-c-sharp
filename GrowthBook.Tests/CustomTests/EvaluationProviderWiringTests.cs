using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers how the two evaluators are put together and what they report, which is the part the move
/// out of <see cref="GrowthBook"/> could quietly change without any test noticing.
/// </summary>
public class EvaluationProviderWiringTests
{
    private sealed class CapturingLogger : ILogger, ILogger<ConditionEvaluationProvider>
    {
        private readonly List<string> _messages;

        public CapturingLogger(List<string> messages) => _messages = messages;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    private static (FeatureEvaluationProvider Features, List<string> Messages) BuildEvaluators()
    {
        var messages = new List<string>();
        var logger = new CapturingLogger(messages);
        var conditions = new ConditionEvaluationProvider(logger);

        var features = new FeatureEvaluationProvider(logger, conditions);
        var experiments = new ExperimentEvaluationProvider(logger, conditions);

        features.ExperimentEvaluator = experiments;
        experiments.FeatureEvaluator = features;

        return (features, messages);
    }

    private static EvaluationContext ContextFor(IDictionary<string, Feature> features, ISet<string> tracked = null, Action<Experiment, ExperimentResult> onTracked = null) =>
        new EvaluationContext(
            new GlobalContext { Features = features, TrackingCallback = onTracked },
            new UserContext
            {
                Attributes = new JObject { ["id"] = "user-1" },
                TrackedExperiments = tracked
            });

    /// <summary>
    /// The filter and rollout decisions moved into <see cref="ExperimentUtilities"/>, which takes an
    /// optional logger. Without it passed, the reasons a rule was skipped stop being reported.
    /// </summary>
    [Fact]
    public void ARuleSkippedByItsFilterSaysWhy()
    {
        var (features, messages) = BuildEvaluators();

        var context = ContextFor(new Dictionary<string, Feature>
        {
            ["flag"] = new Feature
            {
                DefaultValue = JToken.FromObject(false),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Force = JToken.FromObject(true),
                        Filters = new List<Filter>
                        {
                            // No such attribute on the user, so the filter excludes this run.
                            new Filter { Attribute = "missing", Seed = "s", HashVersion = 2, Ranges = new[] { new BucketRange(0d, 0d) } }
                        }
                    }
                }
            }
        });

        features.EvaluateFeature("flag", context);

        messages.Should().Contain(m => m.Contains("filter"),
            "a rule skipped because of a filter has to say so, or an operator cannot tell a miss from a bug");
    }

    /// <summary>
    /// The two evaluators are mutually recursive. Each creating its own peer gave a fresh evaluator
    /// for every prerequisite, so this checks the pair handed in is the pair used.
    /// </summary>
    [Fact]
    public void ThePrerequisiteIsEvaluatedByTheWiredPeer()
    {
        var (features, _) = BuildEvaluators();

        var context = ContextFor(new Dictionary<string, Feature>
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
                        Force = JToken.FromObject(true)
                    }
                }
            }
        });

        features.EvaluateFeature("child", context).Value.Value<bool>().Should().BeTrue();
    }

    /// <summary>
    /// A rule's tracks go through the same exposure path as an experiment assignment, so a caller
    /// that carries a set of what it has already reported sees each one once.
    /// </summary>
    [Fact]
    public void AForcedRulesTracksAreReportedOncePerRequest()
    {
        var (features, _) = BuildEvaluators();

        var reported = new List<string>();
        var tracked = new HashSet<string>();

        var context = ContextFor(
            new Dictionary<string, Feature>
            {
                ["flag"] = new Feature
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
                                    Result = new ExperimentResult { VariationId = 1, HashAttribute = "id", HashValue = "user-1" }
                                }
                            }
                        }
                    }
                }
            },
            tracked,
            (experiment, result) => reported.Add(experiment.Key));

        // Two evaluations of one request: a fresh evaluation context each time, as GrowthBook and
        // the client both build, over the same user - which is what carries the set of exposures
        // already reported.
        features.EvaluateFeature("flag", context);
        features.EvaluateFeature("flag", new EvaluationContext(context.Global, context.User));

        reported.Should().Equal(new[] { "exp-1" },
            "the same exposure reported twice is a duplicate the caller has to pay for and then undo");
    }
}
