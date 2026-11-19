using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class ContextualBanditParsingTests
{
    private const string Timestamp = "2026-10-20T00:00:00.000Z";
    private const string DecryptionKey = "MDEyMzQ1Njc4OWFiY2RlZg==";

    private const string Features = @"{ ""bandit-feature"": { ""defaultValue"": ""default"", ""rules"": [ {
        ""key"": ""bandit-exp"", ""seed"": ""bandit-exp"", ""hashAttribute"": ""id"", ""hashVersion"": 2,
        ""coverage"": 1, ""contextualVariations"": [ ""control"", ""treatment"" ],
        ""weights"": [ 0.5, 0.5 ], ""meta"": [ { ""key"": ""0"" }, { ""key"": ""1"" } ],
        ""contextualBanditRef"": ""cb-bandit"" } ] } }";

    private const string Bandits = @"{ ""cb-bandit"": { ""banditVersion"": 7, ""contexts"": [
        { ""leafId"": 1, ""condition"": { ""signedUpAt"": { ""$gte"": """ + Timestamp + @""" } }, ""weights"": [ 1, 0 ] } ] } }";

    [Fact]
    public void APlainLeafConditionKeepsItsTimestampAsAString()
    {
        var parsed = Parse(@"{ ""features"": " + Features + @", ""contextualBandits"": " + Bandits + " }");

        AssertTimestampIsAString(parsed);
        AssertTheLeafMatchesAUserWithThatTimestamp(parsed);
    }

    [Fact]
    public void AnEncryptedLeafConditionKeepsItsTimestampAsAString()
    {
        var parsed = Parse(@"{ ""features"": " + Features + @", ""encryptedContextualBandits"": """ + Encrypt(Bandits) + @""" }");

        AssertTimestampIsAString(parsed);
        AssertTheLeafMatchesAUserWithThatTimestamp(parsed);
    }

    private static HttpClientExtensions.ParsedPayload Parse(string json) =>
        HttpClientExtensions.ParsePayloadFrom(json, NullLogger<FeatureRefreshWorker>.Instance,
            new GrowthBookConfigurationOptions { DecryptionKey = DecryptionKey });

    private static void AssertTimestampIsAString(HttpClientExtensions.ParsedPayload parsed)
    {
        var condition = parsed.ContextualBandits["cb-bandit"].Contexts[0].Condition["signedUpAt"]["$gte"];

        condition.Type.Should().Be(JTokenType.String);
        condition.ToString(Newtonsoft.Json.Formatting.None).Should().Be($"\"{Timestamp}\"");
    }

    private static void AssertTheLeafMatchesAUserWithThatTimestamp(HttpClientExtensions.ParsedPayload parsed)
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = new JObject { ["id"] = "user-1", ["signedUpAt"] = "2026-11-01T09:30:00.000Z" },
            Features = parsed.Features,
            ContextualBandits = parsed.ContextualBandits
        });

        growthBook.EvalFeature("bandit-feature").ExperimentResult.LeafId.Should().Be(1,
            "the user signed up after the leaf's cut-off, and ISO timestamps compare correctly as strings");
    }

    private static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();

        aes.Key = Convert.FromBase64String(DecryptionKey);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();

        var bytes = Encoding.UTF8.GetBytes(plaintext);

        return $"{Convert.ToBase64String(aes.IV)}.{Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length))}";
    }
}
