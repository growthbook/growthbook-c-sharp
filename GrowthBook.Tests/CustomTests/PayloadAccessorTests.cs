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

    /// <summary>
    /// Captures whatever a bad encrypted payload raises, from construction or from the accessor.
    /// Which layer decrypts is not the point: the requirement is that nothing leaving the SDK
    /// carries the key or the decrypted text, wherever the failure is noticed.
    /// </summary>
    private static Exception DecryptionFailureFor(string encryptedFeatures, string decryptionKey)
    {
        try
        {
            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                EncryptedFeatures = encryptedFeatures,
                DecryptionKey = decryptionKey
            });

            growthBook.GetDecryptedPayload();

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

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
        const string WrongKey = "AAAAAAAAAAAAAAAAAAAAAA==";

        var thrown = DecryptionFailureFor(EncryptedPayload, WrongKey);

        thrown.Should().NotBeNull("a payload that cannot be read has to be reported, not ignored");
        thrown.ToString().Should().NotContain(WrongKey,
            "a decryption key must never reach an exception message or a log");
        thrown.ToString().Should().NotContain("position",
            "nor should a fragment of the garbage plaintext leak out through the JSON parser's message");
    }

    [Fact]
    public void BothAccessorsAreReachableThroughTheInterface()
    {
        using IGrowthBook growthBook = Unencrypted();

        growthBook.GetPayload().Should().NotBeNull();
        growthBook.GetDecryptedPayload().Should().NotBeNull();
    }

    private static string EncryptWith(string plaintext, string key)
    {
        var keyBytes = Convert.FromBase64String(key);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);

        using var aes = System.Security.Cryptography.Aes.Create();

        aes.BlockSize = 128;
        aes.Key = keyBytes;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();

        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return $"{Convert.ToBase64String(aes.IV)}.{Convert.ToBase64String(cipherBytes)}";
    }

    /// <summary>
    /// The outer message is sanitised, but the parser's own error names what it was reading - which
    /// is the decrypted payload. Carried as InnerException it reaches ToString(), so an application
    /// logging the exception logs the content the encryption was there to protect.
    /// </summary>
    [Fact]
    public void AFailureToParseTheDecryptedPayloadDoesNotCarryItsContent()
    {
        const string Secret = "super-secret-flag-name";

        var thrown = DecryptionFailureFor(EncryptWith($"{{\"{Secret}\": this is not valid json }}", DecryptionKey), DecryptionKey);

        thrown.Should().NotBeNull("a payload that cannot be parsed has to be reported, not ignored");
        thrown.ToString().Should().NotContain(Secret,
            "the whole exception is what gets logged, so no part of it may carry decrypted content");
        thrown.ToString().Should().NotContain(DecryptionKey, "and the key is not in it either");
    }

    [Fact]
    public void ReplacingTheFeaturesStopsTheStaleCiphertextBeingExported()
    {
        using var growthBook = Encrypted();

        growthBook.GetPayload()["encryptedFeatures"].Should().NotBeNull("nothing has replaced the features yet");

        growthBook.Features = new Dictionary<string, Feature> { ["fresh-flag"] = Flag(true) };

        var payload = growthBook.GetPayload();

        payload["encryptedFeatures"].Should().BeNull("the ciphertext no longer describes what this instance evaluates");
        payload["features"]["fresh-flag"].Should().NotBeNull("so the payload carries the features actually in effect");
    }

    [Fact]
    public void ReplacingTheFeaturesAlsoStopsTheDecryptedAccessorExportingThem()
    {
        using var growthBook = Encrypted();

        growthBook.Features = new Dictionary<string, Feature> { ["fresh-flag"] = Flag(true) };

        var payload = growthBook.GetDecryptedPayload();

        payload["features"]["fresh-flag"].Should().NotBeNull();
        payload["features"].Children().Should().HaveCount(1, "only the features in effect, not the ones the ciphertext held");
    }

    [Fact]
    public void AnUntouchedInstanceStillExportsItsEncryptedPayload()
    {
        using var growthBook = Encrypted();

        growthBook.GetPayload()["encryptedFeatures"].Value<string>().Should().Be(EncryptedPayload,
            "nothing replaced the features, so the ciphertext still describes them");
        growthBook.GetDecryptedPayload()["features"].Should().NotBeNull();
    }
}
