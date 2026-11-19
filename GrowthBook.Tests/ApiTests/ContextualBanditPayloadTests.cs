using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

/// <summary>
/// Covers the route a contextual bandit definition travels: out of the feature payload, through the refresh worker
/// and repository, and into the evaluation that buckets against its weights.
/// </summary>
public class ContextualBanditPayloadTests
{
    private const string PayloadWithBandits = @"{
        ""features"": { ""bandit-feature"": { ""defaultValue"": ""default"", ""rules"": [ {
            ""key"": ""bandit-exp"", ""seed"": ""bandit-exp"", ""hashAttribute"": ""id"", ""hashVersion"": 2,
            ""coverage"": 1, ""contextualVariations"": [ ""control"", ""treatment"" ],
            ""weights"": [ 0.5, 0.5 ], ""meta"": [ { ""key"": ""0"" }, { ""key"": ""1"" } ],
            ""contextualBanditRef"": ""cb-bandit"" } ] } },
        ""contextualBandits"": { ""cb-bandit"": { ""banditVersion"": 7,
            ""contexts"": [ { ""leafId"": 1, ""condition"": {}, ""weights"": [ 1, 0 ] } ] } }
    }";

    [Fact]
    public async Task RefreshCacheFromApi_ShouldExposeTheContextualBanditsFromThePayload()
    {
        var worker = CreateWorker(PayloadWithBandits);

        await worker.RefreshCacheFromApi();

        var bandits = ((IFeaturePayloadSource)worker).Payload.ContextualBandits;

        bandits.Should().ContainKey("cb-bandit");
        bandits["cb-bandit"].BanditVersion.Should().Be(7);
        bandits["cb-bandit"].Contexts.Should().HaveCount(1);
        bandits["cb-bandit"].Contexts[0].Weights.Should().BeEquivalentTo(new[] { 1d, 0d });
    }

    [Fact]
    public async Task RefreshCacheFromApi_WithUndecryptableBandits_ShouldStillDeliverTheFeatures()
    {
        const string payload = @"{ ""features"": { ""a"": { ""defaultValue"": true } },
                                   ""encryptedContextualBandits"": ""not-actually-encrypted"" }";

        var worker = CreateWorker(payload);

        var features = await worker.RefreshCacheFromApi();

        // Losing the bandits must not cost the caller their features: every rule referencing one just falls back to
        // its own marginal weights.
        features.Should().ContainKey("a");
        ((IFeaturePayloadSource)worker).Payload.ContextualBandits.Should().BeNull();
    }

    /// <summary>
    /// The same rule, but carrying explicit ranges as well. The ranges put every user in the second variation,
    /// the leaf's weights allow only the first, so whichever of the two actually decides is visible in the value.
    /// </summary>
    private const string PayloadWithBanditAndRanges = @"{
        ""features"": { ""bandit-feature"": { ""defaultValue"": ""default"", ""rules"": [ {
            ""key"": ""bandit-exp"", ""seed"": ""bandit-exp"", ""hashAttribute"": ""id"", ""hashVersion"": 2,
            ""coverage"": 1, ""contextualVariations"": [ ""control"", ""treatment"" ],
            ""weights"": [ 0.5, 0.5 ], ""ranges"": [ [ 0, 0 ], [ 0, 1 ] ],
            ""meta"": [ { ""key"": ""0"" }, { ""key"": ""1"" } ],
            ""contextualBanditRef"": ""cb-bandit"" } ] } },
        ""contextualBandits"": { ""cb-bandit"": { ""banditVersion"": 7,
            ""contexts"": [ { ""leafId"": 1, ""condition"": {}, ""weights"": [ 1, 0 ] } ] } }
    }";

    [Fact]
    public async Task TheLeafsWeightsDecideTheAssignmentEvenWhenTheRuleCarriesRanges()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBanditAndRanges);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        await growthBook.LoadFeatures();

        var result = growthBook.EvalFeature("bandit-feature");

        result.Value.ToString().Should().Be("control",
            "the leaf's weights allow only the first variation, so ranges left over from the rule must not be the ones that select it");
        result.ExperimentResult.VariationWeights.Should().BeEquivalentTo(new[] { 1d, 0d },
            "tracking has to report the weights that actually produced the assignment");
    }

    /// <summary>
    /// A variation restored from a sticky bucket was chosen by whatever leaf applied at the time, which is not
    /// recorded anywhere. Reporting the leaf selected now would claim weights of [1, 0] produced a treatment.
    /// </summary>
    [Fact]
    public async Task AReusedStickyAssignmentCarriesNoBanditDetails()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        var saved = new StickyAssignmentsDocument("id", "1",
            new Dictionary<string, string> { ["bandit-exp__0"] = "1" });

        var stickyBucketService = new InMemoryStickyBucketService();
        stickyBucketService.SaveAssignments(saved);

        using var growthBook = new GrowthBook(new Context(new { id = "1" })
        {
            FeatureRepository = repository,
            StickyBucketService = stickyBucketService,
            StickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument> { [saved.FormattedAttribute] = saved }
        });

        await growthBook.LoadFeatures();

        var result = growthBook.EvalFeature("bandit-feature");

        result.Value.ToString().Should().Be("treatment", "the saved assignment is what a sticky bucket is for");
        result.ExperimentResult.VariationWeights.Should().BeNull(
            "weights of [1, 0] could never have selected treatment, so naming them would misreport how this exposure came about");
        result.ExperimentResult.LeafId.Should().BeNull();
        result.ExperimentResult.BanditVersion.Should().BeNull();
    }

    [Fact]
    public async Task LoadFeatures_ShouldBucketAgainstTheWeightsSuppliedByThePayload()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        await growthBook.LoadFeatures();

        var result = growthBook.EvalFeature("bandit-feature");

        // The rule's own weights are an even split; the bandit's leaf sends everyone to the first variation.
        result.Value.ToString().Should().Be("control");
        result.ExperimentResult.LeafId.Should().Be(1);
        result.ExperimentResult.BanditVersion.Should().Be(7);
        result.ExperimentResult.VariationWeights.Should().BeEquivalentTo(new[] { 1d, 0d });
    }

    [Fact]
    public async Task ThePayloadsWeightsAreOnlyUsedBesideTheFeaturesTheyCameWith()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        await growthBook.LoadFeatures();

        growthBook.EvalFeature("bandit-feature").ExperimentResult.BanditVersion.Should().Be(7);

        growthBook.Features = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits).Features;

        growthBook.EvalFeature("bandit-feature").ExperimentResult.BanditVersion.Should().BeNull(
            "these features did not arrive with those weights, so nothing vouches that they belong together");
    }

    [Fact]
    public async Task LoadFeatures_WhenThePayloadHasNoBandits_ShouldKeepTheOnesFromTheContext()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, null)));

        var context = new Context(new { id = "1" })
        {
            FeatureRepository = repository,
            ContextualBandits = payload.ContextualBandits
        };

        using var growthBook = new GrowthBook(context);

        await growthBook.LoadFeatures();

        // A payload that carries no definitions must not wipe out the ones the caller supplied.
        growthBook.EvalFeature("bandit-feature").ExperimentResult.LeafId.Should().Be(1);
    }

    [Fact]
    public async Task LoadFeatures_WhenALaterPayloadWithdrawsABandit_ShouldStopUsingItsWeights()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        await growthBook.LoadFeatures();

        growthBook.EvalFeature("bandit-feature").ExperimentResult.LeafId.Should().Be(1,
            "the first payload supplied the bandit");

        // The backend has withdrawn the bandit; the rules referencing it are still being served.
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, null)));

        await growthBook.LoadFeatures();

        var result = growthBook.EvalFeature("bandit-feature");

        result.ExperimentResult.LeafId.Should().BeNull(
            "a definition the payload no longer carries must not keep bucketing users under a version the backend has stopped publishing");
        result.ExperimentResult.VariationWeights.Should().BeNull();
        result.ExperimentResult.BanditVersion.Should().BeNull();
    }

    [Fact]
    public void UpdateAttributes_ShouldAdoptTheBanditsThatCameWithTheRemoteEvaluation()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        // A remote refresh is the only thing that ever runs here: no load precedes it, so nothing else could
        // have supplied the definitions the new rules reference.
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayloadWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        using var growthBook = new GrowthBook(new Context(new { id = "1" })
        {
            FeatureRepository = repository,
            RemoteEval = true,
            ClientKey = "sdk-key",
            ApiHost = "https://api.example.test"
        });

        growthBook.UpdateAttributes(new { id = "2" });

        var result = growthBook.EvalFeature("bandit-feature");

        result.ExperimentResult.LeafId.Should().Be(1,
            "an attribute change fetches a whole payload, so its bandits have to be adopted alongside its features");
        result.ExperimentResult.BanditVersion.Should().Be(7);
    }

    [Fact]
    public async Task TheTrackingCallbackShouldSeeTheBanditDetailsOfTheExposure()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(new FeaturePayload(payload.Features, payload.ContextualBandits)));

        // The repository owns the de-duplication of tracked exposures; a substitute reports every key as
        // already seen, which would silence the callback for reasons unrelated to this test.
        repository.TryMarkAsTracked(Arg.Any<string>()).Returns(true);

        // Read inside the callback rather than held as a reference. A real callback serializes the exposure and
        // sends it; keeping the object and asserting afterwards would see the fields the caller fills in later
        // and pass whether or not the callback ever saw them.
        var callbacks = 0;
        int? trackedLeafId = null;
        int? trackedBanditVersion = null;
        double[] trackedWeights = null;

        using var growthBook = new GrowthBook(new Context(new { id = "1" })
        {
            FeatureRepository = repository,
            TrackingCallback = (experiment, result) =>
            {
                callbacks++;
                trackedLeafId = result.LeafId;
                trackedBanditVersion = result.BanditVersion;
                trackedWeights = result.VariationWeights?.ToArray();
            }
        });

        await growthBook.LoadFeatures();

        growthBook.EvalFeature("bandit-feature");

        // The callback is what reaches the warehouse, so a null here means the exposure can never be attributed
        // to the weights that produced it, however correct the returned result looks afterwards.
        callbacks.Should().Be(1);
        trackedLeafId.Should().Be(1);
        trackedBanditVersion.Should().Be(7);
        trackedWeights.Should().BeEquivalentTo(new[] { 1d, 0d });
    }

    [Fact]
    public async Task ThePublishedPayloadShouldPairFeaturesWithTheWeightsTheyArrivedWith()
    {
        // The worker writes the cache after publishing, and the cache write is where a reader is most likely to
        // arrive. Blocking inside it puts a reader exactly in that window and asserts what it can see there.
        var cacheWriteStarted = new ManualResetEventSlim(false);
        var releaseCacheWrite = new ManualResetEventSlim(false);
        var cache = Substitute.For<IGrowthBookFeatureCache>();

        cache.RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>())
            .Returns(_ => Task.Run(() =>
            {
                cacheWriteStarted.Set();
                releaseCacheWrite.Wait(TimeSpan.FromSeconds(5));
            }));

        var worker = new FeatureRefreshWorker(
            Substitute.For<ILogger<FeatureRefreshWorker>>(),
            new StubHttpClientFactory(PayloadWithBandits),
            new GrowthBookConfigurationOptions { PreferServerSentEvents = false },
            cache);

        var refresh = worker.RefreshCacheFromApi();

        cacheWriteStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        var observed = ((IFeaturePayloadSource)worker).Payload;

        releaseCacheWrite.Set();
        await refresh;

        observed.Should().NotBeNull("the payload is published before the cache write, so a reader in that window has one to read");
        observed.Features.Should().ContainKey("bandit-feature");
        observed.ContextualBandits.Should().ContainKey("cb-bandit");
        observed.ContextualBandits["cb-bandit"].BanditVersion.Should().Be(7,
            "the features and the weights have to be the ones that arrived together, not one from each of two payloads");
    }

    [Fact]
    public async Task ALoadThatWaitedForARefreshThatBroughtNothingFails()
    {
        var worker = Substitute.For<IGrowthBookFeatureRefreshWorker>();

        worker.RefreshCacheFromApi(Arg.Any<CancellationToken?>()).Returns(Task.FromResult<IDictionary<string, Feature>>(null));

        var repository = new FeatureRepository(Substitute.For<ILogger<FeatureRepository>>(), new InMemoryFeatureCache(60), worker);

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        var result = await growthBook.LoadFeaturesWithResult();

        result.Success.Should().BeFalse("the cache is still empty, so an empty feature set would be passed off as a successful load");
    }

    [Fact]
    public async Task AContextualReadThatWaitedForARefreshThatBroughtNothingReturnsNull()
    {
        var worker = Substitute.For<IGrowthBookFeatureRefreshWorker>();

        worker.RefreshCacheFromApi(Arg.Any<CancellationToken?>()).Returns(Task.FromResult<IDictionary<string, Feature>>(null));

        var repository = new FeatureRepository(Substitute.For<ILogger<FeatureRepository>>(), new InMemoryFeatureCache(60), worker);

        var features = await repository.GetFeaturesWithContext(new Context { RemoteEval = false });

        features.Should().BeNull("a failed load is reported as no features, as GetFeatures reports it");
    }

    private sealed class PayloadShape
    {
        public IDictionary<string, Feature> Features { get; set; }
        public IDictionary<string, ContextualBanditDefinition> ContextualBandits { get; set; }
    }

    private static FeatureRefreshWorker CreateWorker(string payloadJson)
    {
        var config = new GrowthBookConfigurationOptions { PreferServerSentEvents = false };
        var cache = Substitute.For<IGrowthBookFeatureCache>();

        cache.RefreshWith(Arg.Any<IDictionary<string, Feature>>(), Arg.Any<CancellationToken?>()).Returns(Task.CompletedTask);

        return new FeatureRefreshWorker(
            Substitute.For<ILogger<FeatureRefreshWorker>>(),
            new StubHttpClientFactory(payloadJson),
            config,
            cache);
    }

    private sealed class StubHttpClientFactory : HttpClientFactory
    {
        private readonly string _payloadJson;

        public StubHttpClientFactory(string payloadJson) => _payloadJson = payloadJson;

        protected internal override HttpClient CreateClient(System.Func<HttpClient, HttpClient> configure) =>
            configure(new HttpClient(new StubHandler(_payloadJson)));

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _payloadJson;

            public StubHandler(string payloadJson) => _payloadJson = payloadJson;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_payloadJson, Encoding.UTF8, "application/json")
                });
        }
    }

    /// <summary>
    /// With a cache shared between processes, another one can refresh it with a payload this worker never
    /// saw. Replacing those features with the worker's older local copy means the SDK serves values the
    /// cache has already moved past.
    /// </summary>
    [Fact]
    public async Task NewerCachedFeaturesAreNotReplacedByTheWorkersOlderCopy()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var newerFromAnotherProcess = new Dictionary<string, Feature> { ["cache-flag"] = new Feature { DefaultValue = true } };

        var worker = Substitute.For<IGrowthBookFeatureRefreshWorker, IFeaturePayloadSource>();
        ((IFeaturePayloadSource)worker).Payload.Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

        var cache = Substitute.For<IGrowthBookFeatureCache>();
        cache.IsCacheExpired.Returns(false);
        cache.FeatureCount.Returns(newerFromAnotherProcess.Count);
        cache.GetFeatures(Arg.Any<CancellationToken?>()).Returns(newerFromAnotherProcess);

        var repository = new FeatureRepository(Substitute.For<ILogger<FeatureRepository>>(), cache, worker);

        var result = await ((IGrowthBookContextualBanditSource)repository).GetPayload();

        result.Features.Should().ContainKey("cache-flag", "the cache is what is being served, so it decides");
        result.Features.Should().NotContainKey("bandit-feature");
        result.ContextualBandits.Should().BeNull(
            "the definitions this worker holds belong to a payload the cache has moved past, so there is no matching pair to hand over");
    }

    /// <summary>
    /// The same shared cache, but the newer payload keeps the feature keys and changes what they contain - a
    /// revision that edits a bandit's variation list rather than adding or removing flags. That is the ordinary
    /// shape of a change, and matching keys say nothing about whether these are the features those weights were
    /// published with.
    /// </summary>
    [Fact]
    public async Task CachedFeaturesFromAnotherPayloadDoNotBorrowTheWorkersWeights()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var sameKeysDifferentPayload = payload.Features.Keys.ToDictionary(
            key => key,
            _ => new Feature { DefaultValue = "from a payload this worker never saw" });

        var worker = Substitute.For<IGrowthBookFeatureRefreshWorker, IFeaturePayloadSource>();
        ((IFeaturePayloadSource)worker).Payload.Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

        var cache = Substitute.For<IGrowthBookFeatureCache>();
        cache.IsCacheExpired.Returns(false);
        cache.FeatureCount.Returns(sameKeysDifferentPayload.Count);
        cache.GetFeatures(Arg.Any<CancellationToken?>()).Returns(sameKeysDifferentPayload);

        var repository = new FeatureRepository(Substitute.For<ILogger<FeatureRepository>>(), cache, worker);

        var result = await ((IGrowthBookContextualBanditSource)repository).GetPayload();

        result.ContextualBandits.Should().BeNull(
            "a cache holding features alone cannot say which payload they came from, and matching keys are not evidence that it was this one");
    }

    /// <summary>
    /// The built-in cache can hold a payload whole, so it is the one place the pair is known rather than inferred.
    /// A refresh carrying features alone has to retire the definitions stored beside them.
    /// </summary>
    [Fact]
    public async Task AFeaturesOnlyRefreshRetiresTheDefinitionsStoredBeforeIt()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var cache = new InMemoryFeatureCache(60);

        await ((IGrowthBookPayloadCache)cache).RefreshWith(new FeaturePayload(payload.Features, payload.ContextualBandits));

        var stored = await ((IGrowthBookPayloadCache)cache).GetPayload();
        stored.ContextualBandits.Should().ContainKey("cb-bandit", "the payload was stored whole");

        await cache.RefreshWith(payload.Features);

        var afterFeaturesOnly = await ((IGrowthBookPayloadCache)cache).GetPayload();
        afterFeaturesOnly.ContextualBandits.Should().BeNull(
            "a refresh that carries no definitions leaves none that can be claimed to belong to these features");
    }

    /// <summary>
    /// A repository is shared, but a remote evaluation answers for one user. Reading the definitions off
    /// the repository after the call lets a second user's response arrive in between, so the first caller
    /// adopts features evaluated for somebody else.
    /// </summary>
    [Fact]
    public async Task ConcurrentRemoteLoadsEachKeepTheirOwnAnswer()
    {
        var firstUser = new Dictionary<string, Feature> { ["first-user-flag"] = new Feature { DefaultValue = true } };
        var secondUser = new Dictionary<string, Feature> { ["second-user-flag"] = new Feature { DefaultValue = true } };

        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        ((IGrowthBookContextualBanditSource)repository)
            .GetPayloadWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult(true);

                    return Task.Run(async () =>
                    {
                        await secondFinished.Task;
                        return new FeaturePayload(firstUser, null);
                    });
                }

                return Task.FromResult(new FeaturePayload(secondUser, null));
            });

        using var first = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            FeatureRepository = repository,
            RemoteEval = true,
            ClientKey = "sdk-key",
            ApiHost = "https://api.example.test"
        });

        using var second = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-2" }),
            FeatureRepository = repository,
            RemoteEval = true,
            ClientKey = "sdk-key",
            ApiHost = "https://api.example.test"
        });

        var firstLoad = first.LoadFeaturesWithResult();

        await firstEntered.Task;

        await second.LoadFeaturesWithResult();
        secondFinished.TrySetResult(true);

        await firstLoad;

        first.Features.Should().ContainKey("first-user-flag", "each caller keeps the answer evaluated for its own user");
        first.Features.Should().NotContainKey("second-user-flag");
        second.Features.Should().ContainKey("second-user-flag");
    }

    /// <summary>
    /// The published dictionary belongs to every instance built on the repository. Adopting it by
    /// reference means disposing one instance clears it for all of them.
    /// </summary>
    [Fact]
    public async Task DisposingOneInstanceDoesNotEmptyThePublishedPayload()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var published = new FeaturePayload(payload.Features, payload.ContextualBandits);

        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        ((IGrowthBookContextualBanditSource)repository)
            .GetPayload(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(_ => Task.FromResult(published));

        var first = new GrowthBook(new Context { Attributes = JObject.FromObject(new { id = "1" }), FeatureRepository = repository });
        using var second = new GrowthBook(new Context { Attributes = JObject.FromObject(new { id = "2" }), FeatureRepository = repository });

        await first.LoadFeatures();
        await second.LoadFeatures();

        first.Dispose();

        published.Features.Should().ContainKey("bandit-feature", "the published payload is not one instance's to clear");
        second.Features.Should().ContainKey("bandit-feature", "and another instance still holding it must keep its own");

        await second.LoadFeatures();

        second.Features.Should().ContainKey("bandit-feature", "a later load must not adopt an emptied map");
    }
}
