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
    public class FeatureRefreshWorker : IGrowthBookFeatureRefreshWorker, IGrowthBookContextualBanditSource, IDisposable
    {
        private readonly ILogger<FeatureRefreshWorker> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly GrowthBookConfigurationOptions _config;
        private readonly IGrowthBookFeatureCache _cache;
        private readonly LruETagCache _etagCache;
        private readonly string _featuresApiEndpoint;
        private readonly string _serverSentEventsApiEndpoint;
        private bool _isServerSentEventsEnabled;
        private IDictionary<string, ContextualBanditDefinition> _contextualBandits;
        private SSEClient _sseClient;
        private CancellationTokenSource _refreshWorkerCancellation = new CancellationTokenSource();

        public FeatureRefreshWorker(ILogger<FeatureRefreshWorker> logger, IHttpClientFactory httpClientFactory, GrowthBookConfigurationOptions config, IGrowthBookFeatureCache cache)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _cache = cache;
            _etagCache = new LruETagCache(config.EtagCacheSize);

            var hostEndpoint = config.ApiHost;
            var trimmedHostEndpoint = new string(hostEndpoint?.Reverse().SkipWhile(x => x == '/').Reverse().ToArray());

            _featuresApiEndpoint = $"{trimmedHostEndpoint}/api/features/{config.ClientKey}";
            _serverSentEventsApiEndpoint = $"{trimmedHostEndpoint}/sub/{config.ClientKey}";

            _logger.LogDebug("Features GrowthBook API endpoint: \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);
            _logger.LogDebug("Features GrowthBook API endpoint (Server Sent Events): \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Read on the evaluation path while a refresh may be replacing it, so the reference is swapped rather than
        /// mutated and read through <see cref="Volatile"/> - an evaluation sees either the previous payload's
        /// definitions or the new ones, never a half-populated dictionary.
        /// </remarks>
        public IDictionary<string, ContextualBanditDefinition> ContextualBandits => Volatile.Read(ref _contextualBandits);

        public void Cancel()
        {
            _refreshWorkerCancellation.Cancel();
            _sseClient?.Disconnect();
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            _logger.LogInformation("Making an HTTP request to the default Features API endpoint \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);

            var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);

            var response = await httpClient.GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token, _etagCache);

            if (response.IsNotModified)
            {
                if (_cache is InMemoryFeatureCache inMemoryCache)
                {
                    await inMemoryCache.RefreshExpiration(cancellationToken);
                }

                return await _cache.GetFeatures(cancellationToken);
            }

            if (response.Features is null)
            {
                return null;
            }

            // Published before the features so that an evaluation seeing the new features can't be bucketed against
            // the previous payload's weights.
            Volatile.Write(ref _contextualBandits, response.ContextualBandits);

            await _cache.RefreshWith(response.Features, cancellationToken);

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
                
                _sseClient = new SSEClient(sseLogger, _httpClientFactory, _serverSentEventsApiEndpoint, null, ConfiguredClients.ServerSentEventsApiClient);
                
                // Add general event listener for all events (handles data field)
                _sseClient.AddEventListener(null, async (sseEvent) =>
                {
                    if (sseEvent.HasData)
                    {
                        _logger.LogDebug("Received SSE event: {Data}", sseEvent.Data?.Substring(0, Math.Min(sseEvent.Data?.Length ?? 0, 100)));
                        
                        // A streamed payload carries contextual bandits like any other, so the definitions have to be
                        // refreshed here too - otherwise a bandit's weights would freeze at whatever the last polled
                        // payload had while its features kept updating.
                        var payload = Extensions.HttpClientExtensions.ParsePayloadFrom(sseEvent.Data, _logger, _config);

                        Volatile.Write(ref _contextualBandits, payload.ContextualBandits);

                        await _cache.RefreshWith(payload.Features, _refreshWorkerCancellation.Token);
                        
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

        public void Dispose()
        {
            Cancel();
            _sseClient?.Dispose();
            _refreshWorkerCancellation?.Dispose();
        }
    }
}
