using System;
using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Exceptions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the payload accessors. A consumer exporting what the SDK loaded to another tier, or serving a
/// debug endpoint, had no way to read the payload back out - the state was only reachable as live
/// collections, and there was nothing at all for the encrypted case.
/// </summary>
public class PayloadAccessorTests : UnitTest
{
    // A real key/ciphertext pair taken from the conformance fixtures rather than invented, so the
    // decrypted branch is exercised against something the SDK actually accepts.
    private const string EncryptedPayload = "m5ylFM6ndyOJA2OPadubkw==.Uu7ViqgKEt/dWvCyhI46q088PkAEJbnXKf3KPZjf9IEQQ+A8fojNoxw4wIbPX3aj";
    private const string DecryptionKey = "Zvwv/+uhpFDznZ6SX28Yjg==";

    private static Feature Flag(bool value) => new Feature { DefaultValue = value };

    private static GrowthBook Unencrypted() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature> { ["flag"] = Flag(true) },
        Experiments = new List<Experiment> { new Experiment { Key = "exp", Variations = new JArray(0, 1) } },
        SavedGroups = JObject.FromObject(new { admins = new[] { "user-1" } })
    });

    private static GrowthBook Encrypted() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        EncryptedFeatures = EncryptedPayload,
        DecryptionKey = DecryptionKey
    });

    [Fact]
    public void AnEmptyInstanceStillReturnsAPayloadRatherThanNull()
    {
        using var growthBook = new GrowthBook(new Context());

        var payload = growthBook.GetPayload();

        payload.Should().NotBeNull();
        payload["features"].Should().NotBeNull();
        payload["features"].Children().Should().BeEmpty("nothing has been loaded yet");
    }

    [Fact]
    public void ThePayloadCarriesFeaturesExperimentsAndSavedGroups()
    {
        using var growthBook = Unencrypted();

        var payload = growthBook.GetPayload();

        payload["features"]["flag"].Should().NotBeNull();
        payload["experiments"].Should().HaveCount(1);
        payload["savedGroups"]["admins"].Should().NotBeNull();
    }

    [Fact]
    public void ThePayloadUsesTheFieldNamesTheApiAndTheOtherSdksUse()
    {
        using var growthBook = Unencrypted();

        var payload = growthBook.GetPayload();

        payload["features"]["flag"]["defaultValue"].Value<bool>()
            .Should().BeTrue("camelCase is what another tier consuming this payload will look for");
    }

    [Fact]
    public void MutatingTheReturnedPayloadDoesNotReachTheInstance()
    {
        using var growthBook = Unencrypted();

        var payload = growthBook.GetPayload();
        payload.Remove("features");

        growthBook.GetPayload()["features"].Should().NotBeNull("every call builds a fresh object");
        growthBook.IsOn("flag").Should().BeTrue();
    }

    [Fact]
    public void TheDecryptedPayloadEqualsThePlainOneWhenNothingIsEncrypted()
    {
        using var growthBook = Unencrypted();

        JToken.DeepEquals(growthBook.GetDecryptedPayload(), growthBook.GetPayload())
            .Should().BeTrue("there is nothing to decrypt, so the two have to agree");
    }

    [Fact]
    public void AnEncryptedPayloadIsLeftAsReceivedByGetPayload()
    {
        using var growthBook = Encrypted();

        var payload = growthBook.GetPayload();

        payload["encryptedFeatures"].Value<string>().Should().Be(EncryptedPayload);
        payload["features"].Should().BeNull("the raw payload reports the encrypted field, not a decrypted one");
    }

    [Fact]
    public void AnEncryptedPayloadIsDecryptedByGetDecryptedPayload()
    {
        using var growthBook = Encrypted();

        var payload = growthBook.GetDecryptedPayload();

        payload["encryptedFeatures"].Should().BeNull();
        payload["features"]["feature"]["defaultValue"].Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void AWrongKeyFailsWithoutPuttingTheKeyInTheMessage()
    {
        using var growthBook = new GrowthBook(new Context
        {
            EncryptedFeatures = EncryptedPayload,
            DecryptionKey = "AAAAAAAAAAAAAAAAAAAAAA=="
        });

        Action decrypt = () => growthBook.GetDecryptedPayload();

        var thrown = decrypt.Should().Throw<DecryptionException>().Which;

        thrown.Message.Should().NotContain("AAAAAAAAAAAAAAAAAAAAAA==",
            "a decryption key must never reach an exception message or a log");
        thrown.ToString().Should().NotContain("AAAAAAAAAAAAAAAAAAAAAA==");
        thrown.Message.Should().NotContain("position",
            "nor should a fragment of the garbage plaintext leak out through the JSON parser's message");
    }

    [Fact]
    public void BothAccessorsAreReachableThroughTheInterface()
    {
        using IGrowthBook growthBook = Unencrypted();

        growthBook.GetPayload().Should().NotBeNull();
        growthBook.GetDecryptedPayload().Should().NotBeNull();
    }
}
