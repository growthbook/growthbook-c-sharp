using System;
using System.Collections.Generic;
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
using GrowthBook.Api;

namespace GrowthBook.Api.Extensions
{
    public static class HttpClientExtensions
    {
        private sealed class FeaturesResponse
        {
            public int FeatureCount => Features?.Count ?? 0;
            public Dictionary<string, Feature> Features { get; set; }
            public string EncryptedFeatures { get; set; }
        }

        public static async Task<(IDictionary<string, Feature> Features, bool IsServerSentEventsEnabled, bool IsNotModified)> GetFeaturesFrom(
            this HttpClient httpClient, 
            string endpoint, 
            ILogger logger, 
            GrowthBookConfigurationOptions config, 
            CancellationToken cancellationToken,
            LruETagCache etagCache = null,
            IGrowthBookFeatureCache featureCache = null)
        {
            // Create request
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

            // Get ETag from LRU cache (persists even when main cache expires)
            string cachedETag = etagCache?.Get(endpoint);

            // Add If-None-Match header if we have a cached ETag
            if (!string.IsNullOrEmpty(cachedETag))
            {
                try
                {
                    // Remove quotes if present (ETag should be stored without quotes)
                    string etagValue = cachedETag.Trim().Trim('"');
                    
                    // Validate ETag is not empty after trimming
                    if (string.IsNullOrEmpty(etagValue))
                    {
                        logger.LogWarning("ETag is empty after trimming for endpoint '{Endpoint}', skipping If-None-Match header", endpoint);
                    }
                    else
                    {
                        // Check if it's a weak ETag (starts with W/)
                        bool isWeak = etagValue.StartsWith("W/", StringComparison.OrdinalIgnoreCase);
                        string tagValue = etagValue;
                        
                        if (isWeak)
                        {
                            // Remove W/ prefix for weak ETags
                            tagValue = etagValue.Substring(2).TrimStart().Trim('"');
                        }
                        
                        // EntityTagHeaderValue constructor requires valid unquoted string
                        // If the tag contains special characters or starts with non-alphanumeric,
                        // we need to ensure it's properly formatted
                        // Use Parse method which handles quoted strings properly
                        string etagString = isWeak ? $"W/\"{tagValue}\"" : $"\"{tagValue}\"";
                        var etagHeader = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etagString);
                        request.Headers.IfNoneMatch.Add(etagHeader);
                        logger.LogDebug("Sending conditional request with ETag for endpoint '{Endpoint}' with ETag '{ETag}' (weak: {IsWeak})", endpoint, tagValue, isWeak);
                    }
                }
                catch (FormatException ex)
                {
                    logger.LogWarning(ex, "Invalid ETag format '{ETag}' for endpoint '{Endpoint}', skipping If-None-Match header", cachedETag, endpoint);
                    // Continue without If-None-Match header
                }
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            var statusCode = (int)response.StatusCode;

            // Handle 304 Not Modified - refresh cache TTL with existing data
            if (statusCode == 304)
            {
                logger.LogInformation("Received 304 Not Modified for endpoint '{Endpoint}', using cached data", endpoint);

                // Refresh the cache expiration TTL if we have a feature cache
                if (featureCache != null)
                {
                    await featureCache.RefreshExpiration(cancellationToken);
                    logger.LogDebug("Refreshed cache expiration TTL for endpoint '{Endpoint}'", endpoint);
                }

                // Determine if server sent events are enabled (304 responses may still have headers)
                var isServerSentEventsEnabled = response.Headers.TryGetValues(HttpHeaders.ServerSentEvents.Key, out var sseValues) && sseValues.Contains(HttpHeaders.ServerSentEvents.EnabledValue);

                return (null, isServerSentEventsEnabled, true);
            }

            if (!response.IsSuccessStatusCode)
            {
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

            var isServerSentEventsEnabledFromResponse = response.Headers.TryGetValues(HttpHeaders.ServerSentEvents.Key, out var values) && values.Contains(HttpHeaders.ServerSentEvents.EnabledValue);

            logger.LogDebug($"{nameof(FeatureRefreshWorker)} is configured to prefer server sent events and enabled is now '{isServerSentEventsEnabledFromResponse}'");

            var features = ParseFeaturesFrom(json, logger, config);

            // Extract and store ETag in LRU cache
            if (etagCache != null && response.Headers.ETag != null)
            {
                string etag = response.Headers.ETag.Tag?.Trim('"');
                if (!string.IsNullOrEmpty(etag))
                {
                    etagCache.Put(endpoint, etag);
                    logger.LogDebug("Stored ETag in cache for endpoint '{Endpoint}' with ETag '{ETag}'", endpoint, etag);
                }
            }

            return (features, isServerSentEventsEnabledFromResponse, false);
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
