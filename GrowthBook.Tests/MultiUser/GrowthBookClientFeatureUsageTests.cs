using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.MultiUser;

/// <summary>
/// Feature usage on the multi-user client, as in the JavaScript SDK: a global callback told the user too, a per-request
/// callback on <see cref="UserContext"/>, and deduplication carried by the caller on
/// <see cref="UserContext.TrackedFeatureUsage"/>, the way <see cref="UserContext.TrackedExperiments"/> is.
/// </summary>
public class GrowthBookClientFeatureUsageTests
{
    private static async Task<GrowthBookClient> CreateClient(Action<string, FeatureResult, UserContext> onFeatureUsage = null)
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>
            {
                ["parent"] = new Feature { DefaultValue = true },
                ["child"] = new Feature
                {
                    DefaultValue = false,
                    Rules = new List<FeatureRule>
                    {
                        new FeatureRule
                        {
                            Force = true,
                            ParentConditions = new List<ParentCondition> { new ParentCondition { Id = "parent", Condition = new JObject { ["value"] = true } } }
                        }
                    }
                }
            }));

        var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            OnFeatureUsage = onFeatureUsage
        });

        await client.InitializeAsync();

        return client;
    }

    private static UserContext User(string id) => new UserContext { Attributes = new JObject { ["id"] = id } };

    [Fact]
    public async Task TheGlobalCallbackIsToldTheUserTheFeatureWasEvaluatedFor()
    {
        var seen = new List<(string Key, string User)>();
        using var client = await CreateClient((key, _, user) => seen.Add((key, user.Attributes["id"].Value<string>())));

        client.EvalFeature("child", User("user-1"));

        seen.Should().Contain(("child", "user-1")).And.Contain(("parent", "user-1"), "the prerequisite is reported too");
    }

    [Fact]
    public async Task APerRequestCallbackRunsAlongsideTheGlobalOne()
    {
        var global = new List<string>();
        var perRequest = new List<string>();
        using var client = await CreateClient((key, _, _) => global.Add(key));

        var user = User("user-1");
        user.OnFeatureUsage = (key, _) => perRequest.Add(key);

        client.EvalFeature("parent", user);

        global.Should().Equal("parent");
        perRequest.Should().Equal("parent");
    }

    /// <summary>
    /// Without a dictionary from the caller, each call deduplicates only within itself, so the same value is reported
    /// again on the next call; with one kept for the request, it is reported once.
    /// </summary>
    [Fact]
    public async Task DeduplicationLastsAsLongAsTheDictionaryTheCallerKeeps()
    {
        var usages = 0;
        using var client = await CreateClient((_, _, _) => Interlocked.Increment(ref usages));

        client.EvalFeature("parent", User("user-1"));
        client.EvalFeature("parent", User("user-1"));
        usages.Should().Be(2, "nothing carried the reported values from one call to the next");

        usages = 0;
        var tracked = new Dictionary<string, string>();
        var first = User("user-1");
        first.TrackedFeatureUsage = tracked;
        var second = User("user-1");
        second.TrackedFeatureUsage = tracked;

        client.EvalFeature("parent", first);
        client.EvalFeature("parent", second);
        usages.Should().Be(1, "the request kept one dictionary across both calls");
    }

    [Fact]
    public async Task SetFeatureUsageCallbackReplacesTheGlobalOne()
    {
        var first = new List<string>();
        var second = new List<string>();
        using var client = await CreateClient((key, _, _) => first.Add(key));

        client.SetFeatureUsageCallback((key, _, _) => second.Add(key));
        client.EvalFeature("parent", User("user-1"));

        first.Should().BeEmpty();
        second.Should().Equal("parent");
    }

    [Fact]
    public async Task AThrowingCallbackDoesNotChangeTheResultOrStopTheOther()
    {
        var perRequest = new List<string>();
        using var client = await CreateClient((_, _, _) => throw new InvalidOperationException("boom"));

        var user = User("user-1");
        user.OnFeatureUsage = (key, _) => perRequest.Add(key);

        client.IsOn("parent", user).Should().BeTrue();
        perRequest.Should().Equal("parent");
    }
}
