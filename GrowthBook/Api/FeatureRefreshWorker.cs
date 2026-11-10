using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Runtime.CompilerServices;
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
        private readonly CacheWriteState _cacheState;
        private readonly string _featuresApiEndpoint;
        private readonly string _serverSentEventsApiEndpoint;
        private volatile bool _isServerSentEventsEnabled;
        private readonly object _sseClientLock = new object();
        private SSEClient _sseClient;
        private bool _isDisposed;
        private readonly Func<Func<Task>, Task> _startConnection = work => Task.Run(work);
        private CancellationTokenSource _refreshWorkerCancellation = new CancellationTokenSource();

        /// <summary>
        /// One <see cref="CacheWriteState"/> per cache instance, however many workers write to it.
        /// </summary>
        private static readonly ConditionalWeakTable<IGrowthBookFeatureCache, CacheWriteState> CacheStates =
            new ConditionalWeakTable<IGrowthBookFeatureCache, CacheWriteState>();

        /// <summary>
        /// What a worker knows about the cache it writes to: the order of the writes, the size of the payload last
        /// written and the ETags describing it. It belongs to the cache, not to the worker. Instances sharing a cache,
        /// as every instance a <see cref="GrowthBookFactory"/> creates from a context with a cache does, each build a
        /// worker, and with this kept per worker one's older response could replace another's newer one, which a 304
        /// against the other's ETag then marked fresh.
        /// </summary>
        private sealed class CacheWriteState
        {
            public CacheWriteState(int etagCacheSize) => ETags = new LruETagCache(etagCacheSize);

            /// <summary>Held while the cache, <see cref="StoredFeatureCount"/> and the ETags are written, and while they are read together.</summary>
            public readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);

            public readonly LruETagCache ETags;

            public long WriteSequence;

            public long CachedSequence;

            /// <summary>
            /// The size of the payload last written to the cache. It describes the same fact as the cache, and updating
            /// them in separate steps let an overlapping refresh pair one payload's count with another payload's
            /// features, after which a cleared cache is indistinguishable from an empty one and the flags never come back.
            /// </summary>
            public int StoredFeatureCount = -1;
        }

        public FeatureRefreshWorker(ILogger<FeatureRefreshWorker> logger, IHttpClientFactory httpClientFactory, GrowthBookConfigurationOptions config, IGrowthBookFeatureCache cache)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _cache = cache;
            _cacheState = cache is null ? new CacheWriteState(config.EtagCacheSize) : CacheStates.GetValue(cache, _ => new CacheWriteState(config.EtagCacheSize));

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

            lock (_sseClientLock)
            {
                _sseClient?.Disconnect();
            }
        }

        public async Task<IDictionary<string, Feature>> RefreshCacheFromApi(CancellationToken? cancellationToken = null)
        {
            var sequence = Interlocked.Increment(ref _cacheState.WriteSequence);

            _logger.LogInformation("Making an HTTP request to the default Features API endpoint \'{FeaturesApiEndpoint}\'", _featuresApiEndpoint);

            var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient);

            var response = await httpClient.GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token, _cacheState.ETags).ConfigureAwait(false);

            if (response.IsNotModified)
            {
                bool cacheNoLongerHoldsThePayload;

                await _cacheState.Lock.WaitAsync(cancellationToken ?? _refreshWorkerCancellation.Token).ConfigureAwait(false);

                try
                {
                    cacheNoLongerHoldsThePayload = _cache.FeatureCount == 0 && _cacheState.StoredFeatureCount != 0;
                }
                finally
                {
                    _cacheState.Lock.Release();
                }

                if (cacheNoLongerHoldsThePayload)
                {
                    _logger.LogInformation("Server reported no change but the cache no longer holds the payload, discarding the stored ETag and asking for the whole payload");

                    _cacheState.ETags.Remove(_featuresApiEndpoint);

                    response = await httpClient.GetFeaturesFrom(_featuresApiEndpoint, _logger, _config, cancellationToken ?? _refreshWorkerCancellation.Token, _cacheState.ETags).ConfigureAwait(false);
                }
                else
                {
                    if (_cache is IFeatureCacheExpiration expiringCache)
                    {
                        await expiringCache.RefreshExpiration(cancellationToken).ConfigureAwait(false);
                    }

                    return await _cache.GetFeatures(cancellationToken).ConfigureAwait(false);
                }
            }

            if (response.Features is null)
            {
                return null;
            }

            if (!await WriteCacheInOrder(response.Features, response.ETag, sequence, cancellationToken ?? _refreshWorkerCancellation.Token).ConfigureAwait(false))
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

        private async Task<bool> WriteCacheInOrder(IDictionary<string, Feature> features, string etag, long sequence, CancellationToken cancellationToken)
        {
            await _cacheState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (sequence <= _cacheState.CachedSequence)
                {
                    return false;
                }

                await _cache.RefreshWith(features, cancellationToken).ConfigureAwait(false);
                _cacheState.CachedSequence = sequence;
                _cacheState.StoredFeatureCount = features.Count;

                if (etag != null)
                {
                    _cacheState.ETags.Put(_featuresApiEndpoint, etag);
                }

                return true;
            }
            finally
            {
                _cacheState.Lock.Release();
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
                        
                        var sequence = Interlocked.Increment(ref _cacheState.WriteSequence);
                        var features = GetFeaturesFrom(sseEvent.Data);

                        if (features != null && await WriteCacheInOrder(features, null, sequence, _refreshWorkerCancellation.Token).ConfigureAwait(false))
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
