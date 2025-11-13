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
using GrowthBook.Api.SSE;

namespace GrowthBook.Api
{
    public class FeatureRefreshWorker : IGrowthBookFeatureRefreshWorker, IDisposable
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
        private SSEClient _sseClient;
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
            _sseClient?.Disconnect();
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            _logger.LogInformation("Making an HTTP request to the default Features API endpoint '{FeaturesApiEndpoint}'", _featuresApiEndpoint);

            try
            {
                var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);

                var response = await httpClient.GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token);

                if (response.Features is null)
                {
                    _config?.OnFeaturesRefreshed?.Invoke(false);
                    return null;
                }

                await _cache.RefreshWith(response.Features, cancellationToken);
                _config?.OnFeaturesRefreshed?.Invoke(true);

                // Now that the cache has been populated at least once, we need to see if we're allowed
                // to kick off the server sent events listener and make sure we're in the intended mode
                // of operating going forward.

                if (_config.PreferServerSentEvents)
                {
                    _isServerSentEventsEnabled = response.IsServerSentEventsEnabled;
                    EnsureCorrectRefreshModeIsActive();
                }

                return response.Features;
            }
            catch (Exception)
            {
                _config?.OnFeaturesRefreshed?.Invoke(false);
                throw;
            }
        }

        private void EnsureCorrectRefreshModeIsActive()
        {
            if (_isServerSentEventsEnabled)
            {
                if (_sseClient == null || _sseClient.ConnectionStatus == SSEConnectionStatus.Disconnected)
                {
                    _logger.LogDebug("Server sent events are enabled but not connected, starting SSE client now");
                    StartSSEClient();
                }
            }
            else
            {
                if (_sseClient != null && _sseClient.ConnectionStatus != SSEConnectionStatus.Disconnected)
                {
                    _logger.LogDebug("Server sent events are disabled but client is connected, disconnecting now");
                    _sseClient.Disconnect();
                }
            }
        }

        private void StartSSEClient()
        {
            try
            {
                _sseClient?.Dispose();
                
                var sseLogger = _logger as ILogger<SSEClient> ?? 
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<SSEClient>();
                
                _sseClient = new SSEClient(sseLogger, _httpClientFactory, _serverSentEventsApiEndpoint, _config?.StreamingRequestHeaders != null ? new Dictionary<string, string>(_config.StreamingRequestHeaders) : null, ConfiguredClients.ServerSentEventsApiClient);
                
                // Add general event listener for all events (handles data field)
                _sseClient.AddEventListener(null, async (sseEvent) =>
                {
                    if (sseEvent.HasData)
                    {
                        _logger.LogDebug("Received SSE event: {Data}", sseEvent.Data?.Substring(0, Math.Min(sseEvent.Data?.Length ?? 0, 100)));
                        
                        var features = GetFeaturesFrom(sseEvent.Data);
                        await _cache.RefreshWith(features, _refreshWorkerCancellation.Token);
                        _config?.OnFeaturesRefreshed?.Invoke(true);
                        
                        if (!string.IsNullOrEmpty(sseEvent.Id))
                        {
                            _config?.OnStreamingEventId?.Invoke(sseEvent.Id);
                        }
                        
                        _logger.LogInformation("Cache has been refreshed with server sent event features");
                    }
                });

                // Add connection status event handlers
                _sseClient.ConnectionStatusChanged += (status) =>
                {
                    _logger.LogInformation("SSE connection status changed to: {Status}", status);
                };

                _sseClient.ConnectionError += (exception) =>
                {
                    _logger.LogError(exception, "SSE connection error occurred");
                };

                // Start the connection
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _sseClient.ConnectAsync(_refreshWorkerCancellation.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start SSE client");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing SSE client");
            }
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

        public void Dispose()
        {
            Cancel();
            _sseClient?.Dispose();
            _refreshWorkerCancellation?.Dispose();
        }
    }
}
