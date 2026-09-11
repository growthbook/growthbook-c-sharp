using System.Collections.Generic;
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

        var bandits = ((IGrowthBookContextualBanditSource)worker).ContextualBandits;

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
        ((IGrowthBookContextualBanditSource)worker).ContextualBandits.Should().BeNull();
    }

    [Fact]
    public async Task LoadFeatures_ShouldBucketAgainstTheWeightsSuppliedByThePayload()
    {
        var payload = JsonConvert.DeserializeObject<PayloadShape>(PayloadWithBandits);
        var repository = Substitute.For<IGrowthBookFeatureRepository, IGrowthBookContextualBanditSource>();

        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(payload.Features));
        ((IGrowthBookContextualBanditSource)repository).ContextualBandits.Returns(payload.ContextualBandits);

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
        ((IGrowthBookContextualBanditSource)repository).ContextualBandits.Returns((IDictionary<string, ContextualBanditDefinition>)null);

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
