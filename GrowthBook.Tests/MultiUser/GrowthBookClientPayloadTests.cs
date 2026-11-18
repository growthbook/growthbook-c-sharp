using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.MultiUser;

/// <summary>
/// <c>GetPayload</c> and <c>GetDecryptedPayload</c> on the multi-user client, as on the JavaScript SDK's client.
/// </summary>
public class GrowthBookClientPayloadTests
{
    private static async Task<GrowthBookClient> CreateClient(JObject savedGroups = null)
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>
            {
                ["flag"] = new Feature { DefaultValue = true }
            }));

        var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = repository,
            SavedGroups = savedGroups
        });

        await client.InitializeAsync();

        return client;
    }

    [Fact]
    public async Task ThePayloadHoldsTheLoadedFeaturesAndTheSavedGroups()
    {
        using var client = await CreateClient(JObject.Parse(@"{""group-1"": [""a"", ""b""]}"));

        var payload = client.GetPayload();

        payload["features"]["flag"]["defaultValue"].Value<bool>().Should().BeTrue();
        payload["experiments"].Should().BeEquivalentTo(new JArray());
        payload["savedGroups"]["group-1"].ToObject<string[]>().Should().Equal("a", "b");
    }

    [Fact]
    public async Task WithoutSavedGroupsThePayloadLeavesThemOut()
    {
        using var client = await CreateClient();

        client.GetPayload().Should().NotContainKey("savedGroups");
    }

    [Fact]
    public async Task ChangingTheReturnedPayloadDoesNotChangeTheClient()
    {
        var savedGroups = JObject.Parse(@"{""group-1"": [""a""]}");
        using var client = await CreateClient(savedGroups);

        var payload = client.GetPayload();
        payload["features"]["flag"]["defaultValue"] = false;
        ((JArray)payload["savedGroups"]["group-1"]).Add("injected");

        client.GetPayload()["features"]["flag"]["defaultValue"].Value<bool>().Should().BeTrue();
        savedGroups["group-1"].ToObject<string[]>().Should().Equal("a");
    }

    [Fact]
    public async Task TheDecryptedPayloadIsThePayload()
    {
        using var client = await CreateClient();

        JToken.DeepEquals(client.GetDecryptedPayload(), client.GetPayload()).Should().BeTrue("the client never holds ciphertext");
    }

    [Fact]
    public void BeforeTheFirstLoadThePayloadHasNoFeatures()
    {
        using var client = new GrowthBookClient(new Options { ClientKey = "sdk-test", LoggerFactory = NullLoggerFactory.Instance, FeatureRepository = Substitute.For<IGrowthBookFeatureRepository>() });

        ((JObject)client.GetPayload()["features"]).Should().BeEmpty();
    }
}
