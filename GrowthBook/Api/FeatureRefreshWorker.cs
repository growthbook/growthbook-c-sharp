using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using GrowthBook.Extensions;
using GrowthBook.Providers;
using System.Linq;
using System.IO;
using Microsoft.Extensions.Logging;

namespace GrowthBook.Api
{
    public class FeatureRefreshWorker : IGrowthBookFeatureRefreshWorker
    {
        private sealed class FeaturesResponse
        {
            public int FeatureCount => Features?.Count ?? 0;
            public Dictionary<string, Feature> Features { get; set; }
            public string EncryptedFeatures { get; set; }
        }

        private readonly ILogger<FeatureRefreshWorker> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly GrowthBookConfigurationOptions _config;
        private readonly IGrowthBookFeatureCache _cache;
        private readonly string _featuresApiEndpoint;
        private readonly string _serverSentEventsApiEndpoint;
        private bool _isServerSentEventsEnabled;
        private Task _serverSentEventsListener;
        private CancellationTokenSource _serverSentEventsListenerCancellation;
        private CancellationTokenSource _refreshWorkerCancellation = new CancellationTokenSource();

        public FeatureRefreshWorker(ILogger<FeatureRefreshWorker> logger, IHttpClientFactory httpClientFactory, GrowthBookConfigurationOptions config, IGrowthBookFeatureCache cache)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _cache = cache;

            var hostEndpoint = config.ApiHost;
            var trimmedHostEndpoint = new string(hostEndpoint?.Reverse().SkipWhile(x => x == '/').Reverse().ToArray());

            _featuresApiEndpoint = $"{trimmedHostEndpoint}/api/features/{config.ClientKey}";
            _serverSentEventsApiEndpoint = $"{trimmedHostEndpoint}/sub/{config.ClientKey}";

            _logger.LogDebug($"Features GrowthBook API endpoint: '{_featuresApiEndpoint}'");
            _logger.LogDebug($"Features GrowthBook API endpoint (Server Sent Events): '{_featuresApiEndpoint}'");
        }

        public void Cancel()
        {
            _refreshWorkerCancellation.Cancel();
            _serverSentEventsListenerCancellation?.Cancel();
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            _logger.LogInformation($"Making an HTTP request to the default Features API endpoint '{_featuresApiEndpoint}'");

            var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);
            var response = await httpClient.GetAsync(_featuresApiEndpoint, cancellationToken ?? _refreshWorkerCancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"HTTP request to default Features API endpoint '{_featuresApiEndpoint}' resulted in a {response.StatusCode} status code");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();

            _logger.LogDebug($"Read response JSON from default Features API request: '{json}'");

            var features = GetFeaturesFrom(json);
            await _cache.RefreshWith(features, cancellationToken);

            // Now that the cache has been populated at least once, we need to see if we're allowed
            // to kick off the server sent events listener and make sure we're in the intended mode
            // of operating going forward.

            if (_config.PreferServerSentEvents)
            {
                _isServerSentEventsEnabled = response.Headers.TryGetValues("x-sse-support", out var values) && values.Contains("enabled");

                _logger.LogDebug($"{nameof(FeatureRefreshWorker)} is configured to prefer server sent events and enabled is now '{_isServerSentEventsEnabled}'");
                EnsureCorrectRefreshModeIsActive();
            }

            return features;
        }

        private void EnsureCorrectRefreshModeIsActive()
        {
            if (_isServerSentEventsEnabled)
            {
                if (_serverSentEventsListener is null || _serverSentEventsListener.IsCompleted)
                {
                    _logger.LogDebug("Server sent events are enabled but not being listened for, starting the listener now");
                    _serverSentEventsListenerCancellation = new CancellationTokenSource();
                    _serverSentEventsListener = ListenForServerSentEvents();
                }
            }
            else
            {
                if (_serverSentEventsListener != null)
                {
                    _logger.LogDebug("Server sent events are disabled but being listened for, cancelling the listener now");

                    _serverSentEventsListenerCancellation.Cancel();
                    _serverSentEventsListener = null;
                }
            }
        }

        private Task ListenForServerSentEvents()
        {
            return Task.Run(async () =>
            {
                _logger.LogInformation("The listener for server sent events is now running");

                while (!_serverSentEventsListenerCancellation.IsCancellationRequested && !_refreshWorkerCancellation.IsCancellationRequested)
                {
                    try
                    {
                        _logger.LogInformation($"Making an HTTP request to server sent events endpoint '{_serverSentEventsApiEndpoint}'");

                        var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.ServerSentEventsApiClient);
                        var stream = await httpClient.GetStreamAsync(_serverSentEventsApiEndpoint);

                        using (var reader = new StreamReader(stream))
                        {
                            while (!reader.EndOfStream && !_serverSentEventsListenerCancellation.IsCancellationRequested && !_refreshWorkerCancellation.IsCancellationRequested)
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

                                _logger.LogDebug($"Read response JSON from server sent events API request: '{json}'");

                                var features = GetFeaturesFrom(json);

                                await _cache.RefreshWith(features, _serverSentEventsListenerCancellation.Token);

                                _logger.LogInformation("Cache has been refreshed with server sent event features");
                            }
                        }
                    }
                    catch(HttpRequestException ex)
                    {
                        _logger.LogError(ex, $"Encountered an HTTP exception during request to server sent events endpoint '{_serverSentEventsApiEndpoint}'");
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError(ex, $"Encountered an unhandled exception during request to server sent events endpoint '{_serverSentEventsApiEndpoint}'");
                    }
                }

                _logger.LogInformation("The listener for server sent events was cancelled and has ended");
            });
        }

        private IDictionary<string, Feature> GetFeaturesFrom(string json)
        {
            var featuresResponse = JsonConvert.DeserializeObject<FeaturesResponse>(json);

            if (featuresResponse.EncryptedFeatures.IsNullOrWhitespace())
            {
                _logger.LogInformation($"API response JSON contained no encrypted features, returning '{featuresResponse.FeatureCount}' unencrypted features");
                return featuresResponse.Features;
            }

            _logger.LogInformation("API response JSON contained encrypted features, decrypting them now");
            _logger.LogDebug($"Attempting to decrypt features with the provided decryption key '{_config.DecryptionKey}'");

            var decryptedFeaturesJson = featuresResponse.EncryptedFeatures.DecryptWith(_config.DecryptionKey);

            _logger.LogDebug($"Completed attempt to decrypt features which resulted in plaintext value of '{decryptedFeaturesJson}'");

            var jsonObject = JObject.Parse(decryptedFeaturesJson);

            return jsonObject.ToObject<Dictionary<string, Feature>>();
        }
    }
}
