using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// An <see cref="IGrowthBookPlugin"/> that batches experiment and feature evaluation events
    /// and posts them to the GrowthBook ingestor endpoint.
    /// <para>
    /// Events are flushed either when the batch reaches <see cref="TrackingPluginConfig.BatchSize"/>
    /// or when <see cref="TrackingPluginConfig.BatchTimeout"/> elapses, whichever comes first.
    /// All remaining events are flushed on <see cref="Close"/>, which also waits for the batches
    /// already on their way.
    /// </para>
    /// <para>
    /// The plugin is disabled (no HTTP calls are made) when <see cref="TrackingPluginConfig.ClientKey"/>
    /// is null or empty.
    /// </para>
    /// </summary>
    public sealed class GrowthBookTrackingPlugin : IGrowthBookPlugin, IExperimentViewedPlugin, IFeatureEvaluatedPlugin
    {
        private readonly TrackingPluginConfig _config;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly bool _disabled;
        private readonly ILogger _logger;

        private readonly object _lock = new object();
        private readonly List<TrackingEvent> _buffer = new List<TrackingEvent>();

        /// <summary>
        /// The events sent recently, and the order they were last seen in, for deduplication.
        /// </summary>
        private readonly Dictionary<string, LinkedListNode<string>> _recentEvents = new Dictionary<string, LinkedListNode<string>>();
        private readonly LinkedList<string> _recentEventOrder = new LinkedList<string>();

        private Timer _timer;
        private bool _closed;

        /// <summary>
        /// The batches handed over for sending, one after another.
        /// </summary>
        /// <remarks>
        /// A chain rather than a task per batch. Each flush blocks nothing and waits for the one before
        /// it, so a slow ingestor costs one pending send instead of a new worker for every batch; and
        /// <see cref="Close"/> has a single thing to wait on before it may dispose the client.
        /// </remarks>
        private Task _flushChain = Task.CompletedTask;
        private int _pendingBatches;

        public GrowthBookTrackingPlugin(TrackingPluginConfig config, ILogger logger = null)
        {
            _config = config;
            _logger = logger;
            _disabled = string.IsNullOrEmpty(config.ClientKey);

            if (config.HttpClient != null)
            {
                _httpClient = config.HttpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _httpClient = new HttpClient();
                _ownsHttpClient = true;
            }
        }

        public void Init()
        {
            if (_disabled)
            {
                _logger?.LogWarning("GrowthBookTrackingPlugin is disabled");
            }
        }

        public void OnExperimentViewed(Experiment experiment, ExperimentResult result, JObject attributes)
        {
            if (_disabled) return;

            Enqueue(TrackingEvent.ForExperiment(experiment, result, attributes));
        }

        public void OnFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes)
        {
            if (_disabled) return;

            Enqueue(TrackingEvent.ForFeature(featureKey, result, attributes));
        }

        public void Close()
        {
            List<TrackingEvent> toFlush;
            Task inFlight;

            lock (_lock)
            {
                if (_closed) return;
                _closed = true;

                _timer?.Dispose();
                _timer = null;

                toFlush = DrainBuffer();
                inFlight = _flushChain;
            }

            // Batches already taken out of the buffer are the ones most easily lost: the buffer looks
            // empty, so without waiting here the client below would be disposed out from under them.
            try
            {
                if (!inFlight.Wait(_config.ResolvedCloseTimeout()))
                {
                    _logger?.LogWarning("Tracking flushes did not finish within {Timeout}; closing anyway", _config.ResolvedCloseTimeout());
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A tracking flush failed while closing: {Message}", ex.Message);
            }

            if (toFlush.Count > 0)
            {
                try
                {
                    FlushBatchAsync(toFlush).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Final tracking flush failed: {Message}", ex.Message);
                }
            }

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        private void Enqueue(TrackingEvent trackingEvent)
        {
            // Built outside the lock: it is only serialisation, and it is the expensive part.
            var dedupeKey = BuildDedupeKey(trackingEvent);

            lock (_lock)
            {
                // Checked inside the lock: outside it an event can arrive between the final drain and
                // the flag being set, and would sit in a buffer nothing will ever flush. Refusing it is
                // the honest answer, so say so rather than accept something that will never be sent.
                if (_closed)
                {
                    _logger?.LogDebug("Tracking event discarded: the plugin is closed");

                    return;
                }

                if (IsRecentlySeen(dedupeKey))
                {
                    return;
                }

                _buffer.Add(trackingEvent);

                if (_buffer.Count < _config.ResolvedBatchSize())
                {
                    if (_timer == null)
                    {
                        var timeout = _config.ResolvedBatchTimeout();
                        _timer = new Timer(ScheduledFlush, null, timeout, Timeout.InfiniteTimeSpan);
                    }

                    return;
                }

                _timer?.Dispose();
                _timer = null;

                QueueFlush(DrainBuffer());
            }
        }

        private void ScheduledFlush(object state)
        {
            lock (_lock)
            {
                if (_closed) return;

                _timer = null;

                var toFlush = DrainBuffer();

                if (toFlush.Count > 0)
                {
                    QueueFlush(toFlush);
                }
            }
        }

        /// <summary>
        /// Adds a batch to the send chain. Must be called while holding <see cref="_lock"/>.
        /// </summary>
        private void QueueFlush(List<TrackingEvent> batch)
        {
            var maxPending = _config.ResolvedMaxPendingBatches();

            if (_pendingBatches >= maxPending)
            {
                // The ingestor is not keeping up. Holding every batch would grow without limit and take
                // the application down with it, so the batch is dropped and said out loud.
                _logger?.LogWarning(
                    "Dropping a tracking batch of {Count} event(s): {Pending} batches are already waiting to be sent",
                    batch.Count, _pendingBatches);

                return;
            }

            _pendingBatches++;

            _flushChain = _flushChain.ContinueWith(
                async _ =>
                {
                    try
                    {
                        await FlushBatchAsync(batch).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Tracking flush failed: {Message}", ex.Message);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _pendingBatches--;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }

        /// <summary>
        /// Identifies an event for deduplication: what happened, not when or how often.
        /// </summary>
        /// <remarks>
        /// The event name and its properties, matching the JavaScript SDK. Attributes are left out
        /// unless <see cref="TrackingPluginConfig.DedupeKeyAttributes"/> names some, because an
        /// instance already belongs to one user - including them would only make every event unique
        /// again the moment anything unrelated about that user changed.
        /// </remarks>
        private string BuildDedupeKey(TrackingEvent trackingEvent)
        {
            var key = new JObject
            {
                ["eventName"] = trackingEvent.EventName,
                ["properties"] = trackingEvent.Properties
            };

            foreach (var attribute in _config.DedupeKeyAttributes ?? Array.Empty<string>())
            {
                key["attr:" + attribute] = trackingEvent.Attributes?[attribute];
            }

            return key.ToString(Formatting.None);
        }

        /// <summary>
        /// Whether this event has already been sent recently. Must be called while holding <see cref="_lock"/>.
        /// </summary>
        /// <remarks>
        /// A feature is evaluated wherever it is checked, which in a hot path is every request, and
        /// every evaluation raises an event. Without this the ingestor receives the same fact
        /// thousands of times and the batches behind it queue up.
        /// <para>
        /// Least-recently-used, so a key that keeps being seen keeps its place and a cache full of
        /// one-off events still evicts the oldest first.
        /// </para>
        /// </remarks>
        private bool IsRecentlySeen(string dedupeKey)
        {
            if (_recentEvents.TryGetValue(dedupeKey, out var existing))
            {
                _recentEventOrder.Remove(existing);
                _recentEvents[dedupeKey] = _recentEventOrder.AddLast(dedupeKey);

                return true;
            }

            _recentEvents[dedupeKey] = _recentEventOrder.AddLast(dedupeKey);

            while (_recentEvents.Count > _config.ResolvedDedupeCacheSize())
            {
                var oldest = _recentEventOrder.First;

                _recentEventOrder.RemoveFirst();
                _recentEvents.Remove(oldest.Value);
            }

            return false;
        }

        private List<TrackingEvent> DrainBuffer()
        {
            var result = new List<TrackingEvent>(_buffer);
            _buffer.Clear();
            return result;
        }

        private async Task FlushBatchAsync(List<TrackingEvent> batch)
        {
            if (batch.Count == 0 || _disabled) return;

            var url = $"{_config.ResolvedIngestorHost()}/track?client_key={_config.ClientKey}";
            var json = JsonConvert.SerializeObject(batch);

            // Disposed even when the send throws, and the response too - otherwise every batch leaves
            // a request, its content and a response behind for the collector, including when the caller
            // supplied a long-lived HttpClient that keeps them reachable.
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
            {
                request.Headers.TryAddWithoutValidation("User-Agent", SdkMetadata.UserAgent);

                using (var response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // The host, not the full URL: the query string carries the client key.
                        _logger?.LogWarning(
                            "Tracking POST to {Host} returned {Status}",
                            _config.ResolvedIngestorHost(), (int)response.StatusCode);
                    }
                }
            }
        }
    }
}
