using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <c>MergeAttributes</c> builds the merged attributes in a copy and publishes them in one assignment, as
/// <c>updateAttributes</c> does in the JavaScript SDK. It used to write into the object in place.
/// </summary>
public class MergeAttributesTests
{
    private static GrowthBook Create(JObject attributes) => new GrowthBook(new Context
    {
        Enabled = true,
        Attributes = attributes,
        Features = new Dictionary<string, Feature>(),
        LoggerFactory = NullLoggerFactory.Instance
    });

    /// <summary>
    /// An evaluation reads the attributes once and works with that object. Written in place, a merge changed it under
    /// an evaluation still running, which could then see one key from before the merge and another from after it.
    /// </summary>
    [Fact]
    public void AMergeLeavesTheAttributesAnEvaluationAlreadyHoldsUntouched()
    {
        using var gb = Create(new JObject { ["id"] = "u1", ["plan"] = "free" });
        var held = gb.Attributes;

        gb.MergeAttributes(new Dictionary<string, object> { ["plan"] = "pro", ["role"] = "admin" });

        held.ToString(Newtonsoft.Json.Formatting.None).Should().Be(@"{""id"":""u1"",""plan"":""free""}");
        gb.Attributes["plan"].Value<string>().Should().Be("pro");
        gb.Attributes["role"].Value<string>().Should().Be("admin");
        gb.Attributes["id"].Value<string>().Should().Be("u1");
    }

    [Fact]
    public void AMergeDoesNotChangeTheAttributesTheCallerPassedIn()
    {
        var callers = new JObject { ["id"] = "u1" };
        using var gb = Create(callers);

        gb.MergeAttributes(new { role = "admin" });

        callers.Should().NotContainKey("role");
    }

    [Fact]
    public void MergingIntoNoAttributesStartsFromEmpty()
    {
        using var gb = Create(new JObject());
        gb.Attributes = null;

        gb.MergeAttributes(new Dictionary<string, object> { ["id"] = "u1" });

        gb.Attributes["id"].Value<string>().Should().Be("u1");
    }

    /// <summary>
    /// As with the spread in the JavaScript SDK, a key merged with null is kept with a null value.
    /// </summary>
    [Fact]
    public void AKeyMergedWithNullIsKeptAsNull()
    {
        using var gb = Create(new JObject { ["id"] = "u1", ["plan"] = "free" });

        gb.MergeAttributes(new Dictionary<string, object> { ["plan"] = null });

        gb.Attributes.Should().ContainKey("plan");
        gb.Attributes["plan"].Type.Should().Be(JTokenType.Null);
    }

    /// <summary>
    /// Each merge reads the current attributes and publishes the result under one lock, so merges on several threads
    /// keep every key: none is built from attributes another merge has already replaced.
    /// </summary>
    [Fact]
    public void ConcurrentMergesKeepEveryKey()
    {
        using var gb = Create(new JObject { ["id"] = "u1" });

        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            gb.MergeAttributes(new Dictionary<string, object> { [$"key-{i}"] = i }));

        gb.Attributes.Properties().Count(p => p.Name.StartsWith("key-")).Should().Be(400);
    }

    [Fact]
    public void AMergeAfterAnUpdateBuildsOnTheUpdate()
    {
        using var gb = Create(new JObject { ["id"] = "u1" });

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "u2" });
        gb.MergeAttributes(new Dictionary<string, object> { ["plan"] = "pro" });

        gb.Attributes["id"].Value<string>().Should().Be("u2");
        gb.Attributes["plan"].Value<string>().Should().Be("pro");
    }

    /// <summary>
    /// An update and a merge started together must end as one of them followed by the other: the update's attributes,
    /// with or without the merged key. A merge that read the attributes before the update and published after it
    /// would bring the old user's attributes back. Large attributes keep the merge copying long enough for an update
    /// that ignored the lock to land in between.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUpdateAndAMergeAtOnceEndAsOneAfterTheOther(bool updateWithDictionary)
    {
        var previous = new JObject { ["id"] = "old-user" };

        for (var i = 0; i < 2000; i++)
        {
            previous[$"old-{i}"] = i;
        }

        for (var round = 0; round < 200; round++)
        {
            using var gb = Create((JObject)previous.DeepClone());
            using var start = new Barrier(2);

            var update = Task.Run(() =>
            {
                start.SignalAndWait();

                if (updateWithDictionary)
                {
                    gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "new-user" });
                }
                else
                {
                    gb.UpdateAttributes(new { id = "new-user" });
                }
            });

            var merge = Task.Run(() =>
            {
                start.SignalAndWait();
                gb.MergeAttributes(new Dictionary<string, object> { ["plan"] = "pro" });
            });

            Task.WaitAll(update, merge);

            gb.Attributes["id"].Value<string>().Should().Be("new-user", $"the update completed in round {round}, so the old user cannot come back");
            gb.Attributes.Should().NotContainKey("old-0", $"in round {round} a merge built on the old attributes was published over the update");
        }
    }
}
