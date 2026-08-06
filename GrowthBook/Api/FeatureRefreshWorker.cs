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
using GrowthBook.Utilities;
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
        private readonly LruETagCache _etagCache;
        private readonly string _featuresApiEndpoint;
        private readonly string _serverSentEventsApiEndpoint;
        private volatile bool _isServerSentEventsEnabled;
        private readonly object _sseClientLock = new object();
        private IDictionary<string, ContextualBanditDefinition> _contextualBandits;
        private SSEClient _sseClient;
        private bool _isDisposed;
        private readonly Func<Func<Task>, Task> _startConnection = work => Task.Run(work);
        private readonly SemaphoreSlim _cacheWriteLock = new SemaphoreSlim(1, 1);
        private long _cacheWriteSequence;
        private long _cachedSequence;
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

            lock (_sseClientLock)
            {
                _sseClient?.Disconnect();
            }
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            var sequence = Interlocked.Increment(ref _cacheWriteSequence);

            _logger.LogInformation("Making an HTTP request to the default Features API endpoint \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);

            var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);

            var response = await httpClient.GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token, _etagCache).ConfigureAwait(false);

            if (response.IsNotModified)
            {
                if (_cache is InMemoryFeatureCache inMemoryCache)
                {
                    await inMemoryCache.RefreshExpiration(cancellationToken).ConfigureAwait(false);
                }

                return await _cache.GetFeatures(cancellationToken).ConfigureAwait(false);
            }

            if (response.Features is null)
            {
                return null;
            }

            if (!await WriteCacheInOrder(response.Features, response.ETag, response.ContextualBandits, sequence, cancellationToken ?? _refreshWorkerCancellation.Token).ConfigureAwait(false))
            {
                return await _cache.GetFeatures(cancellationToken).ConfigureAwait(false);
            }

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

        private async Task<bool> WriteCacheInOrder(IDictionary<string, Feature> features, string etag, IDictionary<string, ContextualBanditDefinition> contextualBandits, long sequence, CancellationToken cancellationToken)
        {
            await _cacheWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (sequence <= _cachedSequence)
                {
                    return false;
                }

                // Published before the features so that an evaluation seeing the new features can't be bucketed against
                // the previous payload's weights, and only by the write that wins, so a response overtaken by a newer
                // one can't replace its weights either.
                Volatile.Write(ref _contextualBandits, contextualBandits);

                await _cache.RefreshWith(features, cancellationToken).ConfigureAwait(false);
                _cachedSequence = sequence;

                if (etag != null)
                {
                    _etagCache.Put(_featuresApiEndpoint, etag);
                }

                return true;
            }
            finally
            {
                _cacheWriteLock.Release();
            }
        }

        private void NotifyFeaturesStreamed(IDictionary<string, Feature> features)
        {
            var callback = _config?.OnFeaturesStreamed;

            if (callback is null || features is null)
            {
                return;
            }

            try
            {
                callback(features);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The handler for streamed features threw");
            }
        }

        private void EnsureCorrectRefreshModeIsActive()
        {
            lock (_sseClientLock)
            {
                if (_isDisposed)
                {
                    return;
                }

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
        }

        private void StartSSEClient()
        {
            try
            {
                _sseClient?.Dispose();
                
                var sseLogger = _logger as ILogger<SSEClient> ?? 
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<SSEClient>();
                
                var client = new SSEClient(sseLogger, _httpClientFactory, _serverSentEventsApiEndpoint, null, ConfiguredClients.ServerSentEventsApiClient);

                var token = _refreshWorkerCancellation.Token;

                _sseClient = client;
            
                client.AddEventListener(null, async (sseEvent) =>
                {
                    if (sseEvent.HasData)
                    {
                        _logger.LogDebug("Received SSE event: {Data}", sseEvent.Data?.Substring(0, Math.Min(sseEvent.Data?.Length ?? 0, 100)));
                        
                        var sequence = Interlocked.Increment(ref _cacheWriteSequence);

                        // A streamed payload carries contextual bandits like any other, so the definitions have to be
                        // refreshed here too - otherwise a bandit's weights would freeze at whatever the last polled
                        // payload had while its features kept updating.
                        var payload = Extensions.HttpClientExtensions.ParsePayloadFrom(sseEvent.Data, _logger, _config);
                        var features = payload.Features;

                        if (features != null && await WriteCacheInOrder(features, null, payload.ContextualBandits, sequence, _refreshWorkerCancellation.Token).ConfigureAwait(false))
                        {
                            NotifyFeaturesStreamed(features);

                            _logger.LogInformation("Cache has been refreshed with server sent event features");
                        }
                    }
                });

                // Add connection status event handlers
                client.ConnectionStatusChanged += (status) =>
                {
                    _logger.LogInformation("SSE connection status changed to: {Status}", status);
                };

                client.ConnectionError += (exception) =>
                {
                    _logger.LogError(exception, "SSE connection error occurred");
                };

                // Start the connection
                _ = _startConnection(async () =>
                {
                    Task connecting;

                    lock (_sseClientLock)
                    {
                        if (_isDisposed || !ReferenceEquals(_sseClient, client))
                        {
                            return;
                        }

                        connecting = client.ConnectAsync(token);
                    }

                    try
                    {
                        await connecting.ConfigureAwait(false);
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
            var featuresResponse = JsonConvert.DeserializeObject<FeaturesResponse>(json, JsonUtilities.PayloadSettings);

            if (featuresResponse.EncryptedFeatures.IsNullOrWhitespace())
            {
                _logger.LogInformation("API response JSON contained no encrypted features, returning \'{FeaturesResponseFeatureCount}\' unencrypted features", featuresResponse.FeatureCount);
                return featuresResponse.Features;
            }

            _logger.LogInformation("API response JSON contained encrypted features, decrypting them now");

            var decryptedFeaturesJson = featuresResponse.EncryptedFeatures.DecryptWith(_config.DecryptionKey);

            _logger.LogDebug("Decrypted the features payload into \'{CharacterCount}\' characters of JSON", decryptedFeaturesJson?.Length ?? 0);

            return JsonConvert.DeserializeObject<Dictionary<string, Feature>>(decryptedFeaturesJson, JsonUtilities.PayloadSettings);
        }

        public void Dispose()
        {
            Cancel();

            lock (_sseClientLock)
            {
                _isDisposed = true;

                _sseClient?.Dispose();
                _sseClient = null;
            }

            _refreshWorkerCancellation?.Dispose();
        }
    }
}
