using System;
using System.Net.Http;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Configuration for <see cref="GrowthBookTrackingPlugin"/>.
    /// </summary>
    public sealed class TrackingPluginConfig
    {
        /// <summary>The default GrowthBook ingestor endpoint.</summary>
        public const string DefaultIngestorHost = "https://us1.gb-ingest.com";

        /// <summary>Default number of events to accumulate before flushing to the ingestor.</summary>
        public const int DefaultBatchSize = 100;

        /// <summary>Default maximum time to wait before flushing a partial batch.</summary>
        public static readonly TimeSpan DefaultBatchTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Base URL of the GrowthBook ingestor. Defaults to <see cref="DefaultIngestorHost"/> when not set.
        /// Trailing slashes are stripped automatically.
        /// </summary>
        public string IngestorHost { get; set; }

        /// <summary>
        /// The SDK client key used to authenticate with the ingestor.
        /// Setting this to null or empty disables the plugin.
        /// </summary>
        public string ClientKey { get; set; }

        /// <summary>
        /// Maximum number of events in a batch before an immediate flush is triggered.
        /// Defaults to <see cref="DefaultBatchSize"/>.
        /// </summary>
        public int? BatchSize { get; set; }

        /// <summary>
        /// Maximum time to wait before flushing a partial batch.
        /// Defaults to <see cref="DefaultBatchTimeout"/>.
        /// </summary>
        public TimeSpan? BatchTimeout { get; set; }

        /// <summary>
        /// Optional <see cref="System.Net.Http.HttpClient"/> to use for HTTP requests.
        /// When not provided, an internal instance is created and owned by the plugin.
        /// </summary>
        public HttpClient HttpClient { get; set; }

        /// <summary>Returns the effective ingestor host, falling back to <see cref="DefaultIngestorHost"/>.</summary>
        public string ResolvedIngestorHost() =>
            string.IsNullOrEmpty(IngestorHost)
                ? DefaultIngestorHost
                : IngestorHost.TrimEnd('/');

        /// <summary>Returns the effective batch size, falling back to <see cref="DefaultBatchSize"/>.</summary>
        public int ResolvedBatchSize() => BatchSize == null || BatchSize <= 0 ? DefaultBatchSize : BatchSize.Value;

        /// <summary>Returns the effective batch timeout, falling back to <see cref="DefaultBatchTimeout"/>.</summary>
        public TimeSpan ResolvedBatchTimeout() => BatchTimeout == null || BatchTimeout <= TimeSpan.Zero
            ? DefaultBatchTimeout
            : BatchTimeout.Value;
    }
}

