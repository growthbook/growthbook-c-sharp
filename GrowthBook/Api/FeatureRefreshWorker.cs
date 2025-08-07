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
using GrowthBook.Api.Extensions;

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

            _logger.LogDebug("Features GrowthBook API endpoint: \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);
            _logger.LogDebug("Features GrowthBook API endpoint (Server Sent Events): \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);
        }

        public void Cancel()
        {
            _refreshWorkerCancellation.Cancel();
            _serverSentEventsListenerCancellation?.Cancel();
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            _logger.LogInformation("Making an HTTP request to the default Features API endpoint '{FeaturesApiEndpoint}'", _featuresApiEndpoint);

            var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);

            var response = await httpClient
                .GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token)
                .ConfigureAwait(false);

            if (response.Features is null)
            {
                return null;
            }

            await _cache
                .RefreshWith(response.Features, cancellationToken)
                .ConfigureAwait(false);

            if (_config.PreferServerSentEvents)
            {
                _isServerSentEventsEnabled = response.IsServerSentEventsEnabled;
                EnsureCorrectRefreshModeIsActive();
            }

            return response.Features;
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
                        _logger.LogInformation("Making an HTTP request to server sent events endpoint \'{ServerSentEventsApiEndpoint}\'", _serverSentEventsApiEndpoint);

                        var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.ServerSentEventsApiClient);

                        await httpClient.UpdateWithFeaturesStreamFrom(_serverSentEventsApiEndpoint, _logger, _config, _serverSentEventsListenerCancellation.Token, async features =>
                        {
                            await _cache.RefreshWith(features, _serverSentEventsListenerCancellation.Token);

                            _logger.LogInformation("Cache has been refreshed with server sent event features");
                        });
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogError(ex, "Encountered an HTTP exception during request to server sent events endpoint \'{ServerSentEventsApiEndpoint}\'", _serverSentEventsApiEndpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Encountered an unhandled exception during request to server sent events endpoint \'{ServerSentEventsApiEndpoint}\'", _serverSentEventsApiEndpoint);
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
                _logger.LogInformation("API response JSON contained no encrypted features, returning \'{FeaturesResponseFeatureCount}\' unencrypted features", featuresResponse.FeatureCount);
                return featuresResponse.Features;
            }

            _logger.LogInformation("API response JSON contained encrypted features, decrypting them now");
            _logger.LogDebug("Attempting to decrypt features with the provided decryption key \'{ConfigDecryptionKey}\'", _config.DecryptionKey);

            var decryptedFeaturesJson = featuresResponse.EncryptedFeatures.DecryptWith(_config.DecryptionKey);

            _logger.LogDebug("Completed attempt to decrypt features which resulted in plaintext value of \'{DecryptedFeaturesJson}\'", decryptedFeaturesJson);

            var jsonObject = JObject.Parse(decryptedFeaturesJson);

            return jsonObject.ToObject<Dictionary<string, Feature>>();
        }
    }
}
