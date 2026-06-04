using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using GrowthBook.Extensions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using System.Linq;
using GrowthBook.Exceptions;

namespace GrowthBook.Api.Extensions
{
    /// <summary>
    /// Extension methods for HttpClient to fetch and stream GrowthBook features.
    /// </summary>
    public static class HttpClientExtensions
    {

        /// <summary>
        /// Fetch features from the GrowthBook API endpoint.
        /// </summary>
        /// <param name="httpClient">HttpClient instance</param>
        /// <param name="endpoint">API endpoint</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">GrowthBook configuration</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple with features dictionary and SSE enabled flag</returns>
        /// <exception cref="FeatureLoadException">Thrown on HTTP errors</exception>
        public static async Task<(IDictionary<string, Feature>? Features, bool IsServerSentEventsEnabled)> GetFeaturesFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken)
        {
            var response = await GetFeaturesFrom(httpClient, endpoint, logger, config, cancellationToken, etagCache: null);

            return (response.Features, response.IsServerSentEventsEnabled);
        }

        internal static async Task<(IDictionary<string, Feature>? Features, bool IsServerSentEventsEnabled, bool IsNotModified)> GetFeaturesFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken, LruETagCache? etagCache)
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
                    return (null, isServerSentEventsEnabled, true);
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

                var features = ParseFeaturesFrom(json, logger, config);

                if (response.Headers.ETag != null)
                {
                    etagCache?.Put(endpoint, response.Headers.ETag.ToString());
                }

                return (features, isServerSentEventsEnabled, false);
            }
        }

        /// <summary>
        /// Stream feature updates from SSE endpoint.
        /// </summary>
        /// <param name="httpClient">HttpClient instance</param>
        /// <param name="endpoint">API endpoint</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">GrowthBook configuration</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="onFeaturesRetrieved">Callback for processed features</param>
        public static async Task UpdateWithFeaturesStreamFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken, Func<IDictionary<string, Feature>?, Task> onFeaturesRetrieved)
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
        /// Parse features JSON, including decryption if necessary.
        /// </summary>
        /// <param name="json">Raw JSON string</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">GrowthBook configuration</param>
        /// <returns>Dictionary of features or null</returns>
        private static IDictionary<string, Feature>? ParseFeaturesFrom(string json, ILogger logger, GrowthBookConfigurationOptions config)
        {
            var featuresResponseTypeInfo = GrowthBookJsonContext.Default.FeaturesResponse;
            var featuresResponse = JsonSerializer.Deserialize(json, featuresResponseTypeInfo);

            if (featuresResponse == null || featuresResponse.EncryptedFeatures.IsNullOrWhitespace())
            {
                logger.LogInformation($"API response JSON contained no encrypted features, returning '{featuresResponse?.FeatureCount}' unencrypted features");
                return featuresResponse?.Features;
            }

            logger.LogInformation("API response JSON contained encrypted features, decrypting them now");
            logger.LogDebug($"Attempting to decrypt features with the provided decryption key '{config.DecryptionKey}'");

            var decryptedFeaturesJson = featuresResponse.EncryptedFeatures.DecryptWith(config.DecryptionKey);

            logger.LogDebug($"Completed attempt to decrypt features which resulted in plaintext value of '{decryptedFeaturesJson}'");

            if (string.IsNullOrWhiteSpace(decryptedFeaturesJson))
            {
                logger.LogWarning("Decrypted features JSON is null or empty");
                return null;
            }
            var jsonNode = JsonNode.Parse(decryptedFeaturesJson);
            var dictionaryTypeInfo = GrowthBookJsonContext.Default.DictionaryStringFeature;
            return jsonNode?.Deserialize(dictionaryTypeInfo);
        }
    }
}
