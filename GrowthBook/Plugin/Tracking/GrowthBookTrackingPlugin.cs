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
    /// All remaining events are flushed synchronously on <see cref="Close"/>.
    /// </para>
    /// <para>
    /// The plugin is disabled (no HTTP calls are made) when <see cref="TrackingPluginConfig.ClientKey"/>
    /// is null or empty.
    /// </para>
    /// </summary>
    public sealed class GrowthBookTrackingPlugin: IGrowthBookPlugin
    {
        private readonly TrackingPluginConfig _config;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly bool _disabled;
        private readonly ILogger _logger;

        private readonly object _lock = new object();
        private readonly List<TrackingEvent> _buffer = new List<TrackingEvent>();
        private Timer _timer;
        private volatile bool _closed;

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
            if (_disabled || _closed) return;

            Enqueue(TrackingEvent.ForExperiment(experiment, result, attributes));
        }

        public void OnFeatureEvaluated(string featureKey, FeatureResult result, JObject attributes)
        {
            if (_disabled || _closed) return;

            Enqueue(TrackingEvent.ForFeature(featureKey, result, attributes));
        }

        public void Close()
        {
            List<TrackingEvent> toFlush;
            lock (_lock)
            {
                if (_closed) return;
                _closed = true;

                _timer?.Dispose();
                _timer = null;

                toFlush = DrainBuffer();
            }

            if (toFlush.Count > 0)
            {
                try
                {
                    FlushBatch(toFlush);
                }
                catch (System.Exception ex)
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
            List<TrackingEvent> eagerFlush = null;

            lock (_lock)
            {
                _buffer.Add(trackingEvent);

                if (_buffer.Count >= _config.ResolvedBatchSize())
                {
                    _timer?.Dispose();
                    _timer = null;
                    eagerFlush = DrainBuffer();
                } else if (_timer == null)
                {
                    var timeout = _config.ResolvedBatchTimeout();
                    _timer = new Timer(ScheduledFlush, null, timeout, Timeout.InfiniteTimeSpan);
                }
            }

            if (eagerFlush != null)
            {
                Task.Run(() =>
                {
                    try
                    {
                        FlushBatch(eagerFlush);
                    }
                    catch (System.Exception ex)
                    {
                        _logger?.LogWarning(ex, "Tracking flush failed: {Message}", ex.Message);
                    }
                });
            }
        }

        private List<TrackingEvent> DrainBuffer()
        {
           var result = new List<TrackingEvent>(_buffer);
           _buffer.Clear();
           return result;
        }

        private void ScheduledFlush(object state)
        {
            if (_closed) return;

            List<TrackingEvent> toFlush;
            lock (_lock)
            {
                _timer = null;
                toFlush = DrainBuffer();
            }

            if (toFlush.Count > 0)
            {
                Task.Run(() =>
                    {
                        try
                        {
                            FlushBatch(toFlush);
                        }
                        catch (System.Exception ex)
                        {
                            _logger?.LogWarning(ex, "Tracking flush failed: {Message}", ex.Message);
                        }
                    }
                );
            }
        }

        private void FlushBatch(List<TrackingEvent> batch)
        {
            if (batch.Count == 0 || _disabled) return;

            var url = $"{_config.ResolvedIngestorHost()}/track?client_key={_config.ClientKey}";
            var json = JsonConvert.SerializeObject(batch);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("User-Agent", SdkMetadata.UserAgent);

            var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Tracking POST {Url} returned {Status}", url, (int)response.StatusCode);
            }

        }
    }
}
