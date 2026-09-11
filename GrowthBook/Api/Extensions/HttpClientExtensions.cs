using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using GrowthBook.Extensions;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Linq;
using GrowthBook.Exceptions;

namespace GrowthBook.Api.Extensions
{
    public static class HttpClientExtensions
    {
        private sealed class FeaturesResponse
        {
            public int FeatureCount => Features?.Count ?? 0;
            public Dictionary<string, Feature> Features { get; set; }
            public string EncryptedFeatures { get; set; }
            public Dictionary<string, ContextualBanditDefinition> ContextualBandits { get; set; }
            public string EncryptedContextualBandits { get; set; }
        }

        /// <summary>
        /// What a feature payload carried: the features themselves and, when the payload had any, the contextual
        /// bandit definitions a feature rule can reference.
        /// </summary>
        internal sealed class ParsedPayload
        {
            public IDictionary<string, Feature> Features { get; set; }
            public IDictionary<string, ContextualBanditDefinition> ContextualBandits { get; set; }
        }

        public static async Task<(IDictionary<string, Feature> Features, bool IsServerSentEventsEnabled)> GetFeaturesFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken)
        {
            var response = await GetFeaturesFrom(httpClient, endpoint, logger, config, cancellationToken, etagCache: null);

            return (response.Features, response.IsServerSentEventsEnabled);
        }

        internal static async Task<(IDictionary<string, Feature> Features, bool IsServerSentEventsEnabled, bool IsNotModified, IDictionary<string, ContextualBanditDefinition> ContextualBandits)> GetFeaturesFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken, LruETagCache etagCache)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                var cachedETag = etagCache?.Get(endpoint);

                if (etagCache != null && !string.IsNullOrWhiteSpace(cachedETag))
                {
                    try
                    {
                        request.Headers.IfNoneMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Parse(cachedETag));
                    }
                    catch (FormatException ex)
                    {
                        logger.LogWarning(ex, "Invalid cached ETag '{ETag}' for endpoint '{Endpoint}', skipping conditional request", cachedETag, endpoint);
                        etagCache.Remove(endpoint);
                    }
                }

                var response = await httpClient.SendAsync(request, cancellationToken);
                var isServerSentEventsEnabled = response.Headers.TryGetValues(HttpHeaders.ServerSentEvents.Key, out var values) && values.Contains(HttpHeaders.ServerSentEvents.EnabledValue);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    logger.LogInformation("Features API returned 304 Not Modified for endpoint '{Endpoint}'", endpoint);
                    return (null, isServerSentEventsEnabled, true, null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var message = $"Failed to load features from API. HTTP {statusCode} ({response.StatusCode}) for endpoint '{endpoint}'";

                    if (statusCode == 400)
                    {
                        message += ". This usually indicates an invalid ClientKey.";
                    }
                    else if (statusCode == 401)
                    {
                        message += ". Authentication failed - check your ClientKey.";
                    }
                    else if (statusCode == 403)
                    {
                        message += ". Access forbidden - check your ClientKey permissions.";
                    }

                    logger.LogError(message);
                    throw new FeatureLoadException(message, statusCode);
                }

                var json = await response.Content.ReadAsStringAsync();

                logger.LogDebug($"Read response JSON from default Features API request: '{json}'");

                logger.LogDebug($"{nameof(FeatureRefreshWorker)} is configured to prefer server sent events and enabled is now '{isServerSentEventsEnabled}'");

                var payload = ParsePayloadFrom(json, logger, config);

                if (response.Headers.ETag != null)
                {
                    etagCache?.Put(endpoint, response.Headers.ETag.ToString());
                }

                return (payload.Features, isServerSentEventsEnabled, false, payload.ContextualBandits);
            }
        }

        public static async Task UpdateWithFeaturesStreamFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken, Func<IDictionary<string, Feature>, Task> onFeaturesRetrieved)
        {
            var stream = await httpClient.GetStreamAsync(endpoint);

            using (var reader = new StreamReader(stream))
            {
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var json = reader.ReadLine();

                    // All server sent events will have the format "<key>:<value>" and each message
                    // is a single line in the stream. Right now, the only message that we care about
                    // has a key of "data" and value of the JSON data sent from the server, so we're going
                    // to ignore everything that's doesn't contain a "data" key.

                    if (json?.StartsWith("data:") != true)
                    {
                        // No actual JSON data is present, ignore this message.

                        continue;
                    }

                    // Strip off the key and the colon so we can try to deserialize the JSON data. Keep in mind
                    // that the data key might be sent with no actual data present, so we're also checking up front
                    // to see whether we can just drop this as well or if it actually needs processing.

                    json = json.Substring(5).Trim();

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var features = ParseFeaturesFrom(json, logger, config);

                    await onFeaturesRetrieved(features);
                }
            }
        }

        /// <summary>
        /// Reads a feature payload, decrypting whichever parts of it arrived encrypted.
        /// </summary>
        internal static ParsedPayload ParsePayloadFrom(string json, ILogger logger, GrowthBookConfigurationOptions config)
        {
            var featuresResponse = JsonConvert.DeserializeObject<FeaturesResponse>(json);

            return new ParsedPayload
            {
                Features = ParseFeaturesFrom(json, logger, config),
                ContextualBandits = ParseContextualBanditsFrom(featuresResponse, logger, config)
            };
        }

        /// <summary>
        /// Reads the contextual bandit definitions from a payload, from the encrypted field when that is the one
        /// present.
        /// </summary>
        /// <returns>The definitions, or null when the payload carried none.</returns>
        private static IDictionary<string, ContextualBanditDefinition> ParseContextualBanditsFrom(FeaturesResponse featuresResponse, ILogger logger, GrowthBookConfigurationOptions config)
        {
            if (featuresResponse.EncryptedContextualBandits.IsNullOrWhitespace())
            {
                return featuresResponse.ContextualBandits;
            }

            try
            {
                var decrypted = featuresResponse.EncryptedContextualBandits.DecryptWith(config.DecryptionKey);

                return JsonConvert.DeserializeObject<Dictionary<string, ContextualBanditDefinition>>(decrypted);
            }
            catch (Exception ex)
            {
                // A payload we can't decrypt must not cost the caller their features, so the bandits are dropped and
                // every rule referencing one falls back to its own marginal weights.
                logger.LogError(ex, "Failed to decrypt the contextual bandits from the API response, continuing without them");

                return null;
            }
        }

        private static IDictionary<string, Feature> ParseFeaturesFrom(string json, ILogger logger, GrowthBookConfigurationOptions config)
        {
            var featuresResponse = JsonConvert.DeserializeObject<FeaturesResponse>(json);

            if (featuresResponse.EncryptedFeatures.IsNullOrWhitespace())
            {
                logger.LogInformation($"API response JSON contained no encrypted features, returning '{featuresResponse.FeatureCount}' unencrypted features");
                return featuresResponse.Features;
            }

            logger.LogInformation("API response JSON contained encrypted features, decrypting them now");
            logger.LogDebug($"Attempting to decrypt features with the provided decryption key '{config.DecryptionKey}'");

            var decryptedFeaturesJson = featuresResponse.EncryptedFeatures.DecryptWith(config.DecryptionKey);

            logger.LogDebug($"Completed attempt to decrypt features which resulted in plaintext value of '{decryptedFeaturesJson}'");

            var jsonObject = JObject.Parse(decryptedFeaturesJson);

            return jsonObject.ToObject<Dictionary<string, Feature>>();
        }
    }
}
