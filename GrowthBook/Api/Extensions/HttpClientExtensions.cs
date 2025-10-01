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

namespace GrowthBook.Api.Extensions
{
    public static class HttpClientExtensions
    {
        private sealed class FeaturesResponse
        {
            public int FeatureCount => Features?.Count ?? 0;
            public Dictionary<string, Feature>? Features { get; set; }
            public string? EncryptedFeatures { get; set; }
        }

        public static async Task<(IDictionary<string, Feature>? Features, bool IsServerSentEventsEnabled)> GetFeaturesFrom(this HttpClient httpClient, string endpoint, ILogger logger, GrowthBookConfigurationOptions config, CancellationToken cancellationToken)
        {
            var response = await httpClient.GetAsync(endpoint, cancellationToken);

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

            var isServerSentEventsEnabled = response.Headers.TryGetValues(HttpHeaders.ServerSentEvents.Key, out var values) && values.Contains(HttpHeaders.ServerSentEvents.EnabledValue);

            logger.LogDebug($"{nameof(FeatureRefreshWorker)} is configured to prefer server sent events and enabled is now '{isServerSentEventsEnabled}'");

            var features = ParseFeaturesFrom(json, logger, config);

            return (features, isServerSentEventsEnabled);
        }

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

        private static IDictionary<string, Feature>? ParseFeaturesFrom(string json, ILogger logger, GrowthBookConfigurationOptions config)
        {
            var featuresResponse = JsonConvert.DeserializeObject<FeaturesResponse>(json);

            if (featuresResponse == null || featuresResponse.EncryptedFeatures.IsNullOrWhitespace())
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
