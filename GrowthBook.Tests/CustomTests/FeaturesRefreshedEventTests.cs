using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    public class FeaturesRefreshedEventTests
    {
        [Fact]
        public async Task LoadFeaturesWithResult_WhenModified_SetsWasModifiedAndRelaysEvent()
        {
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var gb = new GrowthBook(new Context { FeatureRepository = mockRepository });

            mockRepository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(ci =>
                {
                    mockRepository.FeaturesRefreshed += Raise.EventWith(
                        new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                            new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));
                    return Task.FromResult<IDictionary<string, Feature>>(features);
                });
            FeaturesRefreshedEventArgs captured = null;
            gb.FeaturesRefreshed += (_, e) => captured = e;
            var result = await gb.LoadFeaturesWithResult(new GrowthBookRetrievalOptions {WaitForCompletion = true});
            result.Success.Should().BeTrue();
            result.WasModified.Should().BeTrue();
            captured.Should().NotBeNull();
            captured.WasModified.Should().BeTrue();
            gb.Features.Should().ContainKey("flag");
        }

        [Fact]
        public void BackgroundRefresh_WhenNotModified_DoesNotOverwriteFeatures()
        {
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var gb = new GrowthBook(new Context { FeatureRepository = mockRepository });
            gb.Features.Should().BeEmpty();

            FeaturesRefreshedEventArgs captured = null;
            gb.FeaturesRefreshed += (_, e) => captured = e;

            mockRepository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: false, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

            gb.Features.Should().BeEmpty();
            captured.Should().NotBeNull();
            captured.WasModified.Should().BeFalse();

        }

        /// <summary>
        /// A remotely evaluated instance has an answer computed for its own attributes. The definitions a
        /// shared repository refreshes were evaluated for nobody in particular, so adopting them would
        /// silently put the caller on a different set than the one they asked for.
        /// </summary>
        [Fact]
        public async Task ARemotelyEvaluatedInstanceKeepsItsOwnAnswerWhenASharedRefreshArrives()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var remote = new Dictionary<string, Feature> { { "remote-flag", new Feature { DefaultValue = true } } };
            var shared = new Dictionary<string, Feature> { { "shared-flag", new Feature { DefaultValue = true } } };

            repository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(remote));

            using var gb = new GrowthBook(new Context
            {
                FeatureRepository = repository,
                RemoteEval = true,
                ClientKey = "sdk-key",
                ApiHost = "https://api.example.test"
            });

            await gb.LoadFeaturesWithResult();

            gb.Features.Should().ContainKey("remote-flag");

            // A local instance on the same repository polls, and the worker announces it to everyone.
            repository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(shared), DateTimeOffset.UtcNow));

            gb.Features.Should().ContainKey("remote-flag",
                "a refresh of the common definitions is not this user's own answer");
            gb.Features.Should().NotContainKey("shared-flag");
        }

        /// <summary>
        /// The refresh has already been announced to the consumer by the time the older load returns, so
        /// letting it win leaves the instance evaluating definitions it has told everyone were replaced.
        /// </summary>
        [Fact]
        public async Task ALoadInFlightDoesNotUndoARefreshThatArrivedWhileItWaited()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var older = new Dictionary<string, Feature> { { "older-flag", new Feature { DefaultValue = true } } };
            var newer = new Dictionary<string, Feature> { { "newer-flag", new Feature { DefaultValue = true } } };

            using var gb = new GrowthBook(new Context { FeatureRepository = repository });

            repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(_ =>
                {
                    // Lands while this load is still in flight, which is the whole point.
                    repository.FeaturesRefreshed += Raise.EventWith(
                        new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.ServerSentEvent,
                            new Dictionary<string, Feature>(newer), DateTimeOffset.UtcNow));

                    return Task.FromResult<IDictionary<string, Feature>>(older);
                });

            var result = await gb.LoadFeaturesWithResult();

            result.Success.Should().BeTrue();
            gb.Features.Should().ContainKey("newer-flag",
                "the refresh is newer than anything this load was waiting on, and the consumer has already been told about it");
            gb.Features.Should().NotContainKey("older-flag");
        }

        /// <summary>
        /// Two loads overlapping must not report each other's response. Read off shared instance state,
        /// whichever response landed last decided what both of them returned.
        /// </summary>
        [Fact]
        public async Task OverlappingLoadsEachReportTheirOwnResponse()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var notModifiedEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var modifiedAnnounced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;

            using var gb = new GrowthBook(new Context { FeatureRepository = repository });

            repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(_ =>
                {
                    var call = Interlocked.Increment(ref calls);

                    if (call == 1)
                    {
                        // The 304 load announces its own response, then waits for the 200 load to announce
                        // a different one before returning.
                        repository.FeaturesRefreshed += Raise.EventWith(
                            new FeaturesRefreshedEventArgs(wasModified: false, FeatureRefreshSource.Http,
                                new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

                        notModifiedEntered.TrySetResult(true);

                        return Task.Run(async () =>
                        {
                            await modifiedAnnounced.Task;
                            return (IDictionary<string, Feature>)features;
                        });
                    }

                    repository.FeaturesRefreshed += Raise.EventWith(
                        new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                            new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

                    modifiedAnnounced.TrySetResult(true);

                    return Task.FromResult<IDictionary<string, Feature>>(features);
                });

            var notModifiedLoad = gb.LoadFeaturesWithResult(new GrowthBookRetrievalOptions { WaitForCompletion = true });

            await notModifiedEntered.Task;

            var modifiedLoad = await gb.LoadFeaturesWithResult(new GrowthBookRetrievalOptions { WaitForCompletion = true });
            var notModified = await notModifiedLoad;

            modifiedLoad.WasModified.Should().BeTrue("this load's own response was a 200");
            notModified.WasModified.Should().BeFalse(
                "this load's own response was a 304, whatever the other load heard afterwards");
        }

        /// <summary>
        /// The event passes through two relays - the repository to each instance, and each instance to its
        /// own consumers - and a thrown exception stops the delegate at whichever relay it reaches. Both
        /// have to isolate, so both are covered here; the faulty subscriber is registered first in each
        /// case, since one registered last blocks nobody.
        /// </summary>
        [Fact]
        public void AThrowingSubscriberOnTheRepositoryDoesNotSilenceTheInstancesBehindIt()
        {
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };
            var worker = Substitute.For<IGrowthBookFeatureRefreshWorker>();

            // A real repository, because the relay being tested is its own.
            var repository = new global::GrowthBook.Api.FeatureRepository(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<global::GrowthBook.Api.FeatureRepository>.Instance,
                Substitute.For<IGrowthBookFeatureCache>(),
                worker);

            // Registered before the instance, so the instance's own subscription comes after it.
            repository.FeaturesRefreshed += (_, _) => throw new InvalidOperationException("another listener is broken");

            using var gb = new GrowthBook(new Context { FeatureRepository = repository });

            var heard = false;
            gb.FeaturesRefreshed += (_, _) => heard = true;

            worker.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

            heard.Should().BeTrue("an instance subscribed after a faulty listener still has to hear the refresh");
            gb.Features.Should().ContainKey("flag", "and its features still have to be updated");
        }

        [Fact]
        public void AThrowingConsumerSubscriberDoesNotSilenceTheOthers()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };

            using var first = new GrowthBook(new Context { FeatureRepository = repository });
            using var second = new GrowthBook(new Context { FeatureRepository = repository });

            first.FeaturesRefreshed += (_, _) => throw new InvalidOperationException("first consumer is broken");

            var firstHeardAfterTheThrow = false;
            var secondHeard = false;

            first.FeaturesRefreshed += (_, _) => firstHeardAfterTheThrow = true;
            second.FeaturesRefreshed += (_, _) => secondHeard = true;

            repository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

            firstHeardAfterTheThrow.Should().BeTrue("a subscriber after the faulty one still has to be called");
            secondHeard.Should().BeTrue("and so does an instance that merely shares the repository");
            second.Features.Should().ContainKey("flag", "its features must still be updated");
        }


        /// <summary>
        /// Subscribers are invoked one at a time so that one cannot silence the others, which lets a
        /// later refresh overtake an earlier one partway through its round. Two instances sharing a
        /// repository must still end on the same payload, so an older refresh is recognised by where it
        /// falls in the fetch order rather than by when it happens to arrive.
        /// </summary>
        [Fact]
        public void ARefreshThatArrivesAfterANewerOneIsNotInstalled()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var older = new Dictionary<string, Feature> { { "older-flag", new Feature { DefaultValue = true } } };
            var newer = new Dictionary<string, Feature> { { "newer-flag", new Feature { DefaultValue = true } } };

            using var gb = new GrowthBook(new Context { FeatureRepository = repository });

            repository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(newer), DateTimeOffset.UtcNow, sequence: 2));

            gb.Features.Should().ContainKey("newer-flag");

            // The earlier fetch finishes its round late and reaches this instance after the later one.
            repository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(older), DateTimeOffset.UtcNow, sequence: 1));

            gb.Features.Should().ContainKey("newer-flag",
                "a refresh fetched earlier is older however late it arrives");
            gb.Features.Should().NotContainKey("older-flag");
        }

        [Fact]
        public void AnUnnumberedRefreshIsStillInstalled()
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "flag", new Feature { DefaultValue = true } } };

            using var gb = new GrowthBook(new Context { FeatureRepository = repository });

            // A source that predates numbering leaves it at zero; what cannot be ordered is taken as it
            // arrives, rather than silently dropped.
            repository.FeaturesRefreshed += Raise.EventWith(
                new FeaturesRefreshedEventArgs(wasModified: true, FeatureRefreshSource.Http,
                    new Dictionary<string, Feature>(features), DateTimeOffset.UtcNow));

            gb.Features.Should().ContainKey("flag");
        }

        /// <summary>
        /// A rule's prerequisites are looked up one after another while the rule is being evaluated.
        /// Reading the feature map afresh at each lookup let a refresh land between them, so one
        /// evaluation answered from two payloads - this rule against the next payload's values.
        /// </summary>
        [Fact]
        public void ARefreshDuringAnEvaluationDoesNotSplitItAcrossTwoPayloads()
        {
            var gated = new Feature
            {
                DefaultValue = "default",
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Force = "forced",
                        ParentConditions = new List<ParentCondition>
                        {
                            new ParentCondition { Id = "first-gate", Condition = JObject.Parse(@"{ ""value"": true }") },
                            new ParentCondition { Id = "second-gate", Condition = JObject.Parse(@"{ ""value"": true }") }
                        }
                    }
                }
            };

            // The first gate runs an experiment, which is what gives the test a hook that fires in the
            // middle of the evaluation - between the two prerequisite lookups.
            var firstGate = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Variations = new JArray(true, true),
                        Coverage = 1d,
                        Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
                    }
                }
            };

            var before = new Dictionary<string, Feature>
            {
                ["gated"] = gated,
                ["first-gate"] = firstGate,
                ["second-gate"] = new Feature { DefaultValue = true }
            };

            var after = new Dictionary<string, Feature>
            {
                ["gated"] = gated,
                ["first-gate"] = firstGate,
                ["second-gate"] = new Feature { DefaultValue = false }
            };

            var swapped = false;
            GrowthBook gb = null;

            gb = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                Features = before,
                TrackingCallback = (_, _) =>
                {
                    if (swapped)
                    {
                        return;
                    }

                    swapped = true;

                    // Stands in for a background refresh landing mid-evaluation.
                    gb.Features = after;
                }
            });

            using var instance = gb;

            var result = gb.EvalFeature("gated");

            swapped.Should().BeTrue("the hook has to have fired, or this test proves nothing");

            result.Value.ToString().Should().Be("forced",
                "both prerequisites belong to the payload the evaluation started from, so a swap between them must not decide the outcome");
        }

        [Fact]
        public void CreateSuccess_ShouldKeepTheOverloadCompiledCallersBindTo()
        {
            var overload = typeof(FeatureLoadResult).GetMethod(
                nameof(FeatureLoadResult.CreateSuccess),
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null);

            overload.Should().NotBeNull(
                "an assembly built against the earlier SDK calls CreateSuccess(int) and would throw MissingMethodException without it");

            var result = FeatureLoadResult.CreateSuccess(3);

            result.Success.Should().BeTrue();
            result.FeatureCount.Should().Be(3);
            result.WasModified.Should().BeTrue("the single-argument overload has to behave as it did before");
        }
    }
}
