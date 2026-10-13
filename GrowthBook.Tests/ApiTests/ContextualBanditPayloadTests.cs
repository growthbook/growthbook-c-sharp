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
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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

        var bandits = ((IGrowthBookContextualBanditSource)worker).Payload.ContextualBandits;

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
        ((IGrowthBookContextualBanditSource)worker).Payload.ContextualBandits.Should().BeNull();
    }

    [Fact]
    public async Task LoadFeatures_ShouldBucketAgainstTheWeightsSuppliedByThePayload()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

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
    public async Task LoadFeatures_WhenThePayloadHasNoBandits_ShouldKeepTheOnesFromTheContext()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, null));

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
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

        using var growthBook = new GrowthBook(new Context(new { id = "1" }) { FeatureRepository = repository });

        await growthBook.LoadFeatures();

        growthBook.EvalFeature("bandit-feature").ExperimentResult.LeafId.Should().Be(1,
            "the first payload supplied the bandit");

        // The backend has withdrawn the bandit; the rules referencing it are still being served.
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, null));

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
        repository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

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
        ((IGrowthBookContextualBanditSource)repository).Payload
            .Returns(new FeaturePayload(payload.Features, payload.ContextualBandits));

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

        var observed = ((IGrowthBookContextualBanditSource)worker).Payload;

        releaseCacheWrite.Set();
        await refresh;

        observed.Should().NotBeNull("the payload is published before the cache write, so a reader in that window has one to read");
        observed.Features.Should().ContainKey("bandit-feature");
        observed.ContextualBandits.Should().ContainKey("cb-bandit");
        observed.ContextualBandits["cb-bandit"].BanditVersion.Should().Be(7,
            "the features and the weights have to be the ones that arrived together, not one from each of two payloads");
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
}
