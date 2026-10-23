using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <see cref="InMemoryStickyBucketService"/> is both read from and written to while experiments are
/// being evaluated: the evaluator asks it for existing assignments and saves new ones as it makes
/// them. One service instance shared by concurrent evaluations is the usage this SDK recommends, so
/// those two happen at once.
/// </summary>
public class StickyBucketServiceConcurrencyTests
{
    private static StickyAssignmentsDocument Document(string value) =>
        new StickyAssignmentsDocument("id", value, new Dictionary<string, string> { ["experiment"] = "0" });

    /// <summary>
    /// An assignment already saved has to stay findable while other assignments are being saved. A
    /// lookup that misses it is a user silently rebucketed, which is the whole thing sticky bucketing
    /// exists to prevent - and it needs no exception to happen.
    /// </summary>
    [Fact]
    public void AnAssignmentAlreadySavedIsFoundWhileOthersAreBeingSaved()
    {
        var service = new InMemoryStickyBucketService();

        service.SaveAssignments(Document("user-fixed"));

        var writing = true;
        var reads = 0;
        var misses = 0;

        using var readerIsRunning = new ManualResetEventSlim(false);

        var reader = Task.Run(() =>
        {
            readerIsRunning.Set();

            while (Volatile.Read(ref writing))
            {
                if (service.GetAssignments("id", "user-fixed") is null)
                {
                    Interlocked.Increment(ref misses);
                }

                Interlocked.Increment(ref reads);
            }
        });

        var writer = Task.Run(() =>
        {
            readerIsRunning.Wait(TimeSpan.FromSeconds(10));

            try
            {
                for (var i = 0; i < 50000; i++)
                {
                    service.SaveAssignments(Document($"user-{i}"));
                }
            }
            finally
            {
                Volatile.Write(ref writing, false);
            }
        });

        Task.WaitAll(reader, writer);

        reads.Should().BeGreaterThan(0, "a run where the reader never read proves nothing");
        misses.Should().Be(0,
            "the assignment was saved before any of this started, so every lookup has to find it");
    }

    [Fact]
    public void ConcurrentWritersAllLand()
    {
        const int Writers = 8;
        const int PerWriter = 2000;

        var service = new InMemoryStickyBucketService();

        using var start = new ManualResetEventSlim(false);

        var tasks = new Task[Writers];

        for (var w = 0; w < Writers; w++)
        {
            var writer = w;

            tasks[w] = Task.Run(() =>
            {
                start.Wait(TimeSpan.FromSeconds(10));

                for (var i = 0; i < PerWriter; i++)
                {
                    service.SaveAssignments(Document($"w{writer}-{i}"));
                }
            });
        }

        start.Set();
        Task.WaitAll(tasks);

        var everything = new List<string>();

        for (var w = 0; w < Writers; w++)
        {
            for (var i = 0; i < PerWriter; i++)
            {
                everything.Add($"id||w{w}-{i}");
            }
        }

        service.GetAllAssignments(everything).Should().HaveCount(Writers * PerWriter,
            "a lost write is an assignment a user would silently be moved off");
    }
}
