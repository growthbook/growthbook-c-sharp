using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Exceptions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers bootstrapping an instance from an encrypted payload held locally, with no network call.
/// The reference SDK's <c>decryptPayload</c> assigns the decrypted result over <c>data.features</c>, so
/// an encrypted payload takes precedence over a plaintext one.
/// </summary>
public class ContextEncryptedFeaturesTests
{
    // AES-128 needs exactly 16 key bytes; these are base64 of two distinct 16-character keys.
    private const string DecryptionKey = "MDEyMzQ1Njc4OWFiY2RlZg==";
    private const string DifferentDecryptionKey = "ZmVkY2JhOTg3NjU0MzIxMA==";

    /// <summary>
    /// Produces a payload in the format the SDK expects: base64 IV, a dot, then base64 AES-CBC ciphertext.
    /// Mirrors what the GrowthBook API returns so the tests exercise the real decryption path rather than a
    /// stand-in for it.
    /// </summary>
    private static string Encrypt(string plaintext, string key = DecryptionKey)
    {
        var keyBytes = Convert.FromBase64String(key);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        using var aes = Aes.Create();

        aes.BlockSize = 128;
        aes.Key = keyBytes;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();

        var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return $"{Convert.ToBase64String(aes.IV)}.{Convert.ToBase64String(cipherBytes)}";
    }

    private const string FeaturePayload = @"{""encrypted-flag"":{""defaultValue"":true}}";

    [Fact]
    public void AnEncryptedPayloadIsDecryptedAndEvaluatedWithoutAnyNetworkCall()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DecryptionKey
        });

        growthBook.Features.Should().ContainKey("encrypted-flag");
        growthBook.IsOn("encrypted-flag").Should().BeTrue("because the payload was decrypted at construction, with no call to LoadFeatures");
    }

    [Fact]
    public void AnEncryptedPayloadTakesPrecedenceOverPlaintextFeatures()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["plaintext-flag"] = new Feature { DefaultValue = true } },
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DecryptionKey
        });

        growthBook.Features.Should().ContainKey("encrypted-flag");
        growthBook.Features.Should().NotContainKey("plaintext-flag",
            "because the reference SDK's decryptPayload assigns the decrypted result over data.features");
    }

    [Fact]
    public void AWrongDecryptionKeyThrowsInsteadOfProducingAnEmptyFeatureSet()
    {
        Action construct = () => new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DifferentDecryptionKey
        });

        construct.Should().Throw<DecryptionException>(
            "because silently yielding zero features would make every flag read as off with no signal that the key is wrong");
    }

    [Fact]
    public void AMalformedPayloadThrows()
    {
        Action construct = () => new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = "this-is-not-a-payload",
            DecryptionKey = DecryptionKey
        });

        construct.Should().Throw<DecryptionException>();
    }

    [Fact]
    public void CiphertextThatDecryptsToSomethingOtherThanFeaturesThrows()
    {
        Action construct = () => new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = Encrypt("not json at all"),
            DecryptionKey = DecryptionKey
        });

        construct.Should().Throw<DecryptionException>("because a payload that decrypts but does not parse is still a payload we cannot use");
    }

    [Fact]
    public void EncryptedFeaturesWithoutADecryptionKeyThrows()
    {
        Action construct = () => new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = Encrypt(FeaturePayload)
        });

        construct.Should().Throw<ArgumentException>("because there is no way to read the payload and pretending otherwise hides the misconfiguration");
    }

    [Fact]
    public void AContextWithoutEncryptedFeaturesIsUnaffected()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["plaintext-flag"] = new Feature { DefaultValue = true } }
        });

        growthBook.Features.Should().ContainKey("plaintext-flag");
        growthBook.IsOn("plaintext-flag").Should().BeTrue();
    }

    [Fact]
    public void AnEmptyEncryptedFeaturesValueIsIgnoredRatherThanTreatedAsAFailure()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["plaintext-flag"] = new Feature { DefaultValue = true } },
            EncryptedFeatures = "   "
        });

        growthBook.IsOn("plaintext-flag").Should().BeTrue("because an unset payload is not a configuration error");
    }

    [Fact]
    public void ADecryptedPayloadSurvivesContextCloning()
    {
        var context = new Context
        {
            Attributes = new JObject(),
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DecryptionKey
        };

        using var factory = new GrowthBookFactory(context);

        var first = factory.CreateForUser(new { id = "user-1" });
        var second = factory.CreateForUser(new { id = "user-2" });

        first.IsOn("encrypted-flag").Should().BeTrue();
        second.IsOn("encrypted-flag").Should().BeTrue("because Clone carries the encrypted payload, so every scoped instance can decrypt it");
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly string _json;

        public SingleResponseHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json)
            });
    }

    private sealed class SingleHandlerHttpClientFactory : HttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        protected internal override HttpClient CreateClient(Func<HttpClient, HttpClient> configure)
            => configure(new HttpClient(_handler, disposeHandler: false));
    }

    [Fact]
    public async Task TheDecryptionKeyIsNeverWrittenToTheLogOnTheApiPathEither()
    {
        // The constructor path and the API-response path decrypt in different places, so pinning one says
        // nothing about the other. This drives FeatureRefreshWorker, which is where the key used to be
        // logged verbatim alongside the full decrypted payload.
        var log = new StringWriter();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(log));
        });

        var responseJson = "{\"encryptedFeatures\":\"" + Encrypt(FeaturePayload) + "\"}";

        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://api.example.com",
            ClientKey = "test-client-key",
            DecryptionKey = DecryptionKey,
            PreferServerSentEvents = false
        };

        var cache = Substitute.For<IGrowthBookFeatureCache>();

        using var worker = new FeatureRefreshWorker(
            loggerFactory.CreateLogger<FeatureRefreshWorker>(),
            new SingleHandlerHttpClientFactory(new SingleResponseHandler(responseJson)),
            config,
            cache);

        var features = await worker.RefreshCacheFromApi();

        features.Should().ContainKey("encrypted-flag", "because the API path still has to decrypt correctly");

        var captured = log.ToString();

        captured.Should().NotContain(DecryptionKey, "because the key must not reach a log sink from any path");
        captured.Should().NotContain("defaultValue", "because the decrypted payload must not be logged either");
    }

    [Fact]
    public void TheDecryptionKeyIsNeverWrittenToTheLogOnTheStreamingPathEither()
    {
        // RefreshCacheFromApi decrypts through HttpClientExtensions, while server-sent events go through
        // the worker's own private GetFeaturesFrom. Both used to log the key and the plaintext, so both
        // need pinning - reached by reflection here rather than by standing up an SSE stream.
        var log = new StringWriter();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(log));
        });

        var config = new GrowthBookConfigurationOptions
        {
            ApiHost = "https://api.example.com",
            ClientKey = "test-client-key",
            DecryptionKey = DecryptionKey,
            PreferServerSentEvents = false
        };

        using var worker = new FeatureRefreshWorker(
            loggerFactory.CreateLogger<FeatureRefreshWorker>(),
            new SingleHandlerHttpClientFactory(new SingleResponseHandler("{}")),
            config,
            Substitute.For<IGrowthBookFeatureCache>());

        var getFeaturesFrom = typeof(FeatureRefreshWorker)
            .GetMethod("GetFeaturesFrom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        getFeaturesFrom.Should().NotBeNull("because this test pins the logging inside that method");

        var streamedJson = "{\"encryptedFeatures\":\"" + Encrypt(FeaturePayload) + "\"}";
        var features = (IDictionary<string, Feature>)getFeaturesFrom.Invoke(worker, new object[] { streamedJson });

        features.Should().ContainKey("encrypted-flag", "because the streaming path still has to decrypt correctly");

        var captured = log.ToString();

        captured.Should().NotContain(DecryptionKey, "because the key must not reach a log sink from the streaming path either");
        captured.Should().NotContain("defaultValue", "because the decrypted payload must not be logged either");
    }

    [Fact]
    public void TheDecryptionKeyIsNeverWrittenToTheLogWhenDecryptingFromTheContext()
    {
        var log = new StringWriter();
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(log));
        });

        using (loggerFactory)
        {
            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                EncryptedFeatures = Encrypt(FeaturePayload),
                DecryptionKey = DecryptionKey,
                LoggerFactory = loggerFactory
            });

            growthBook.IsOn("encrypted-flag").Should().BeTrue();
        }

        var captured = log.ToString();

        captured.Should().NotContain(DecryptionKey, "because the decryption key is the only thing protecting the payload and a log sink is not a secret store");
        captured.Should().NotContain("encrypted-flag\":{\"defaultValue", "because logging the decrypted payload defeats the point of encrypting it");
    }

    /// <summary>
    /// The async evaluation methods load from the repository before evaluating. With a payload supplied
    /// on the Context there is nothing to load and - with no client key - nowhere to load it from, so the
    /// load is at best a wasted request and at worst replaces the decrypted features with its own result.
    /// </summary>
    [Fact]
    public async Task AnEncryptedPayloadIsEvaluatedByTheAsyncMethodsWithoutAnyNetworkCall()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DecryptionKey
        });

        (await growthBook.IsOnAsync("encrypted-flag"))
            .Should().BeTrue("because the payload is already here - evaluating it must not depend on an API the caller never configured");

        (await growthBook.EvalFeatureAsync("encrypted-flag")).On.Should().BeTrue();
        (await growthBook.GetFeatureValueAsync("encrypted-flag", false)).Should().BeTrue();

        growthBook.Features.Should().ContainKey("encrypted-flag",
            "and the decrypted features are still the ones in effect afterwards");
    }

    [Fact]
    public async Task PlaintextContextFeaturesAreAlsoEvaluatedByTheAsyncMethodsWithoutANetworkCall()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["local-flag"] = new Feature { DefaultValue = true } }
        });

        (await growthBook.IsOnAsync("local-flag")).Should().BeTrue(
            "because an inline payload with no client key is the same offline case");
    }

    /// <summary>
    /// The load still has to happen when there is something to load: a client key means the caller
    /// expects the API to be consulted, whatever else they supplied up front.
    /// </summary>
    [Fact]
    public async Task AClientKeyStillMakesTheAsyncMethodsLoadFromTheRepository()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();
        repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(
                new Dictionary<string, Feature> { ["from-api"] = new Feature { DefaultValue = true } }));

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            ClientKey = "sdk-key",
            EncryptedFeatures = Encrypt(FeaturePayload),
            DecryptionKey = DecryptionKey,
            FeatureRepository = repository
        });

        (await growthBook.IsOnAsync("from-api")).Should().BeTrue(
            "because a configured repository is still consulted - this fix is about there being nothing to consult");
    }

    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly TextWriter _writer;

        public CapturingLoggerProvider(TextWriter writer) => _writer = writer;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CapturingLogger(_writer);

        public void Dispose() { }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly TextWriter _writer;

            public CapturingLogger(TextWriter writer) => _writer = writer;

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (_writer)
                {
                    _writer.WriteLine(formatter(state, exception));
                }
            }
        }
    }
}
