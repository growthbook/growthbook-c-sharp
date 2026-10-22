using System;
using System.Collections.Generic;
using System.Net.Http;

namespace GrowthBook.Plugin
{
    /// <summary>
    /// Configuration for <see cref="GrowthBookTrackingPlugin"/>.
    /// </summary>
    public sealed class TrackingPluginConfig
    {
        /// <summary>The default GrowthBook ingestor endpoint.</summary>
        /// <remarks>
        /// The same default the other SDKs and the GrowthBook back end use. Set
        /// <see cref="IngestorHost"/> to <c>https://eu-west-1.gb-ingest.com</c> if the Managed Warehouse
        /// data region is eu-west-1, otherwise the events are dropped on arrival.
        /// </remarks>
        public const string DefaultIngestorHost = "https://us-east-1.gb-ingest.com";

        /// <summary>Default number of events to accumulate before flushing to the ingestor.</summary>
        public const int DefaultBatchSize = 100;

        /// <summary>Default maximum time to wait before flushing a partial batch.</summary>
        public static readonly TimeSpan DefaultBatchTimeout = TimeSpan.FromSeconds(10);

        /// <summary>Default number of batches allowed to be waiting to be sent.</summary>
        public const int DefaultMaxPendingBatches = 10;

        /// <summary>Default time <see cref="GrowthBookTrackingPlugin.Close"/> waits for sends in flight.</summary>
        public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Default number of recently sent events remembered for deduplication.</summary>
        public const int DefaultDedupeCacheSize = 1000;

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
        /// How many batches may be waiting to be sent before further ones are dropped.
        /// Defaults to <see cref="DefaultMaxPendingBatches"/>.
        /// </summary>
        /// <remarks>
        /// Batches are sent one at a time, so this bounds the memory a slow or unreachable ingestor can
        /// cost. Once the limit is reached, new batches are dropped with a warning rather than queued.
        /// </remarks>
        public int? MaxPendingBatches { get; set; }

        /// <summary>
        /// How long <see cref="GrowthBookTrackingPlugin.Close"/> waits for sends already in flight.
        /// Defaults to <see cref="DefaultCloseTimeout"/>.
        /// </summary>
        public TimeSpan? CloseTimeout { get; set; }

        /// <summary>
        /// How many recently sent events are remembered so that repeats are not sent again.
        /// Defaults to <see cref="DefaultDedupeCacheSize"/>.
        /// </summary>
        /// <remarks>
        /// A feature is evaluated wherever it is checked, so in a hot path the same fact would be sent
        /// on every request. Events are identified by name and properties; see
        /// <see cref="DedupeKeyAttributes"/> to take attributes into account as well.
        /// </remarks>
        public int? DedupeCacheSize { get; set; }

        /// <summary>
        /// Attributes that make two otherwise identical events distinct.
        /// </summary>
        /// <remarks>
        /// Empty by default, because an instance already belongs to one user. Name attributes here
        /// when one instance serves more than one identity and the same evaluation for two of them
        /// has to be reported twice.
        /// </remarks>
        public IList<string> DedupeKeyAttributes { get; set; }

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

        /// <summary>Returns the effective pending-batch limit, falling back to <see cref="DefaultMaxPendingBatches"/>.</summary>
        public int ResolvedMaxPendingBatches() =>
            MaxPendingBatches == null || MaxPendingBatches <= 0
                ? DefaultMaxPendingBatches
                : MaxPendingBatches.Value;

        /// <summary>Returns the effective dedupe cache size, falling back to <see cref="DefaultDedupeCacheSize"/>.</summary>
        public int ResolvedDedupeCacheSize() =>
            DedupeCacheSize == null || DedupeCacheSize <= 0
                ? DefaultDedupeCacheSize
                : DedupeCacheSize.Value;

        /// <summary>Returns the effective close timeout, falling back to <see cref="DefaultCloseTimeout"/>.</summary>
        public TimeSpan ResolvedCloseTimeout() =>
            CloseTimeout == null || CloseTimeout <= TimeSpan.Zero
                ? DefaultCloseTimeout
                : CloseTimeout.Value;
    }
}

