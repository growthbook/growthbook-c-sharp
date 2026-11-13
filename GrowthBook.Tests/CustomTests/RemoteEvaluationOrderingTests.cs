using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// A remotely evaluated instance installs only the answer to its newest request: one made for attributes it no longer
/// has must not replace it, whichever arrives last.
/// </summary>
public class RemoteEvaluationOrderingTests
{
    /// <summary>Answers each remote evaluation only when the test says so, and records what each one asked.</summary>
    private sealed class ControlledRepository
    {
        private readonly List<(Context Request, TaskCompletionSource<IDictionary<string, Feature>> Answer)> _calls =
            new List<(Context, TaskCompletionSource<IDictionary<string, Feature>>)>();

        public IGrowthBookFeatureRepository Repository { get; } = Substitute.For<IGrowthBookFeatureRepository>();

        public ControlledRepository()
        {
            Repository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(call =>
                {
                    var answer = new TaskCompletionSource<IDictionary<string, Feature>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_calls) _calls.Add((call.Arg<Context>(), answer));
                    return answer.Task;
                });
        }

        public int Count { get { lock (_calls) return _calls.Count; } }

        public Context Request(int index) { lock (_calls) return _calls[index].Request; }

        public void Answer(int index, string value)
        {
            TaskCompletionSource<IDictionary<string, Feature>> answer;
            lock (_calls) answer = _calls[index].Answer;
            answer.SetResult(new Dictionary<string, Feature> { ["flag"] = new Feature { DefaultValue = value } });
        }

        public void WaitFor(int count) =>
            SpinWait.SpinUntil(() => Count >= count, TimeSpan.FromSeconds(5)).Should().BeTrue($"{count} remote evaluations should have been asked for");
    }

    private static GrowthBookSdk.GrowthBook Create(ControlledRepository controlled, string userId) => new GrowthBookSdk.GrowthBook(new Context
    {
        RemoteEval = true,
        ClientKey = "sdk-test",
        ApiHost = "https://api.example.test",
        Attributes = new JObject { ["id"] = userId },
        FeatureRepository = controlled.Repository,
        LoggerFactory = NullLoggerFactory.Instance
    });

    private static string Flag(GrowthBookSdk.GrowthBook gb) => gb.GetFeatureValue("flag", "none");

    /// <summary>
    /// The request used to be built before the new attributes were assigned, so the server evaluated the previous user.
    /// </summary>
    [Fact]
    public void UpdatingTheAttributesEvaluatesForTheNewOnes()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });

        controlled.WaitFor(1);
        controlled.Request(0).Attributes["id"].Value<string>().Should().Be("user-2");
    }

    [Fact]
    public void AnOlderAnswerArrivingLastDoesNotReplaceANewerOne()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });
        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-3" });
        controlled.WaitFor(2);

        controlled.Answer(1, "for-user-3");
        SpinWait.SpinUntil(() => Flag(gb) == "for-user-3", TimeSpan.FromSeconds(5)).Should().BeTrue();

        controlled.Answer(0, "for-user-2");
        Thread.Sleep(200);

        Flag(gb).Should().Be("for-user-3", "the answer for user-2 was asked for before the attributes changed to user-3");
    }

    /// <summary>
    /// An answer for attributes the instance no longer has is not installed even when it arrives first: until the newer
    /// one comes, the instance keeps what it had rather than another user's features.
    /// </summary>
    [Fact]
    public void AnAnswerForAttributesNoLongerHeldIsNotInstalled()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });
        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-3" });
        controlled.WaitFor(2);

        controlled.Answer(0, "for-user-2");
        Thread.Sleep(200);
        Flag(gb).Should().Be("none");

        controlled.Answer(1, "for-user-3");
        SpinWait.SpinUntil(() => Flag(gb) == "for-user-3", TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    /// <summary>
    /// A load whose request is overtaken by an attribute change waits for the newer evaluation rather than reporting
    /// success with features for the old attributes, or with none.
    /// </summary>
    [Fact]
    public async Task ALoadOvertakenByAnAttributeChangeWaitsForTheNewerEvaluation()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");

        var load = gb.LoadFeaturesWithResult();
        controlled.WaitFor(1);

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });
        controlled.WaitFor(2);

        controlled.Answer(0, "for-user-1");
        await Task.Delay(200);
        load.IsCompleted.Should().BeFalse("its answer was for user-1, and the instance now has user-2");

        controlled.Answer(1, "for-user-2");
        var result = await load.WaitAsync(TimeSpan.FromSeconds(5));

        result.Success.Should().BeTrue();
        Flag(gb).Should().Be("for-user-2");
    }

    [Fact]
    public async Task ALoadWaitingForANewerEvaluationCanBeCancelled()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");
        using var cancellation = new CancellationTokenSource();

        var load = gb.LoadFeaturesWithResult(cancellationToken: cancellation.Token);
        controlled.WaitFor(1);

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-2" });
        controlled.WaitFor(2);

        controlled.Answer(0, "for-user-1");
        await Task.Delay(100);
        cancellation.Cancel();

        var result = await load.WaitAsync(TimeSpan.FromSeconds(5));

        result.Success.Should().BeFalse("the caller gave up before the newer evaluation answered");
    }

    [Fact]
    public async Task ALoadNobodyOvertakesInstallsItsAnswer()
    {
        var controlled = new ControlledRepository();
        using var gb = Create(controlled, "user-1");

        var load = gb.LoadFeaturesWithResult();
        controlled.WaitFor(1);
        controlled.Answer(0, "for-user-1");

        (await load.WaitAsync(TimeSpan.FromSeconds(5))).Success.Should().BeTrue();
        Flag(gb).Should().Be("for-user-1");
    }
}
