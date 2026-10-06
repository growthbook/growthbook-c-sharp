using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using GrowthBook.MultiUser.Configuration;
using GrowthBook.Providers;
using GrowthBook.Utilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.MultiUser
{
    /// <summary>
    /// Thread-safe singleton client for multiuser server-side feature flagging and A/B testing.
    /// Register as a singleton in DI, then pass a per-request <see cref="UserContext"/> to each evaluation call.
    /// Call <see cref="InitializeAsync"/> once after construction to load features before serving requests.
    /// </summary>
    public class GrowthBookClient : IDisposable
    {
        private readonly Options _options;
        private readonly IGrowthBookFeatureRepository _repository;
        private readonly IConditionEvaluationProvider _conditionEvaluator;
        private readonly FeatureEvaluationProvider _featureEvaluator;
        private readonly ExperimentEvaluationProvider _experimentEvaluator;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;
        private readonly bool _ownsRepository;
        private volatile IDictionary<string, Feature> _currentFeatures = new Dictionary<string, Feature>();

        /// <summary>
        /// Orders the feature sets. Refreshes overlap, and one that started earlier carries older
        /// definitions however late it comes back.
        /// </summary>
        private long _refreshSequence;
        private long _publishedRefresh;
        private readonly object _publishLock = new object();
        private bool _disposed;

        /// <summary>
        /// Creates a new <see cref="GrowthBookClient"/> with the given options.
        /// If <see cref="Options.FeatureRepository"/> is not set, a repository is created automatically
        /// using <see cref="Options.ClientKey"/> and <see cref="Options.ApiHost"/>.
        /// </summary>
        public GrowthBookClient(Options options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (options.LoggerFactory != null)
            {
                _loggerFactory = options.LoggerFactory;
                _ownsLoggerFactory = false;
            }
            else
            {
                _loggerFactory = LoggerFactory.Create(builder => { });
                _ownsLoggerFactory = true;
            }

            _conditionEvaluator = new ConditionEvaluationProvider(_loggerFactory.CreateLogger<ConditionEvaluationProvider>());
            _experimentEvaluator = new ExperimentEvaluationProvider(
                _loggerFactory.CreateLogger<ExperimentEvaluationProvider>(), _conditionEvaluator);
            _featureEvaluator = new FeatureEvaluationProvider(
                _loggerFactory.CreateLogger<FeatureEvaluationProvider>(), _conditionEvaluator);

            if (options.FeatureRepository != null)
            {
                _repository = options.FeatureRepository;
                _ownsRepository = false;
            }
            else
            {
                _repository = CreateRepository(options, _loggerFactory, success =>
                {
                    if (!success)
                    {
                        options.OnFeaturesRefreshed?.Invoke(false);

                        return;
                    }

                    // The refreshed features are installed before anyone is told the refresh happened,
                    // so a handler that evaluates does not see the previous set.
                    _ = Task.Run(async () =>
                    {
                        var sequence = Interlocked.Increment(ref _refreshSequence);
                        var features = await _repository.GetFeatures(null);

                        PublishFeatures(features, sequence);

                        options.OnFeaturesRefreshed?.Invoke(true);
                    });
                });
                _ownsRepository = true;
            }
        }

        /// <summary>
        /// Loads features from the repository. Call once at application startup before serving requests.
        /// Invokes <see cref="Options.OnFeaturesRefreshed"/> on success.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            var sequence = Interlocked.Increment(ref _refreshSequence);
            var features = await _repository.GetFeatures(null, ct);
            if (features != null)
            {
                PublishFeatures(features, sequence);
                // With a custom repository the internal callback is not registered,
                // so fire OnFeaturesRefreshed explicitly. With the default repository
                // the internal callback already fires it.
                if (!_ownsRepository)
                {
                    _options.OnFeaturesRefreshed?.Invoke(true);
                }
            }
        }

        /// <summary>
        /// Forces a refresh of features from the API, bypassing the cache.
        /// Invokes <see cref="Options.OnFeaturesRefreshed"/> on success.
        /// </summary>
        public async Task RefreshFeaturesAsync(CancellationToken ct = default)
        {
            var sequence = Interlocked.Increment(ref _refreshSequence);
            var features = await _repository.GetFeatures(
                new GrowthBookRetrievalOptions { ForceRefresh = true, WaitForCompletion = true}, ct);

            if (features != null)
            {
                PublishFeatures(features, sequence);
                if (!_ownsRepository)
                {
                    _options.OnFeaturesRefreshed?.Invoke(true);
                }
            }
        }

        /// <summary>
        /// Replaces the global attributes applied to every evaluation. User attributes take precedence over these.
        /// </summary>
        public void SetGlobalAttributes(JObject attributes)
            => _options.GlobalAttributes = attributes;

        /// <summary>
        /// Replaces the global forced variations map, overriding experiment assignments for all users.
        /// </summary>
        public void SetGlobalForcedVariations(IDictionary<string, int> forcedVariations)
            => _options.GlobalForcedVariations = forcedVariations;

        /// <summary>
        /// Replaces the global forced feature values, overriding feature evaluation results for all users.
        /// </summary>
        public void SetGlobalForcedFeatureValues(IDictionary<string, JToken> forcedFeatureValues)
            => _options.GlobalForcedFeatureValues = forcedFeatureValues;

        /// <summary>
        /// Replaces the tracking callback used to report experiment assignments to your analytics system.
        /// </summary>
        public void SetTrackingCallback(Action<Experiment, ExperimentResult> callback)
            => _options.TrackingCallback = callback;

        /// <summary>Returns the current global attributes, or null if none are set.</summary>
        public JObject GetGlobalAttributes()
            => _options.GlobalAttributes;

        /// <summary>
        /// Returns a snapshot of the currently loaded features. A copy, so a caller changing it cannot
        /// change what every other user in the process evaluates against.
        /// </summary>
        public IDictionary<string, Feature> GetFeatures()
        {
            var features = _currentFeatures;

            if (features == null)
            {
                return new Dictionary<string, Feature>();
            }

            return new Dictionary<string, Feature>(features);
        }

        /// <summary>Cancels the background feature refresh worker and releases resources.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ownsRepository)
            {
                _repository.Cancel();
            }

            if (_ownsLoggerFactory && _loggerFactory is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        /// <summary>Returns true if the feature is enabled for the given user.</summary>
        public bool IsOn(string key, UserContext userContext)
            => EvalFeature(key, userContext).On;

        /// <summary>Returns true if the feature is disabled for the given user.</summary>
        public bool IsOff(string key, UserContext userContext)
            => !IsOn(key, userContext);

        /// <summary>Evaluates a feature flag for the given user and returns the full result.</summary>
        public FeatureResult EvalFeature(string key, UserContext userContext)
        {
            var context = BuildEvaluationContext(userContext);
            return _featureEvaluator.EvaluateFeature(key, context);
        }

        /// <summary>Runs an inline experiment for the given user and returns the assigned variation result.</summary>
        public ExperimentResult Run(Experiment experiment, UserContext userContext)
        {
            var context = BuildEvaluationContext(userContext, experiment);
            return _experimentEvaluator.RunExperiment(experiment, null, context);
        }

        /// <summary>
        /// Evaluates a feature and returns its value as <typeparamref name="T"/>.
        /// Returns <paramref name="fallback"/> if the feature is off, null, or cannot be deserialized.
        /// </summary>
        public T GetFeature<T>(string key, T fallback, UserContext userContext)
        {
            var result = EvalFeature(key, userContext);
            if (result.Value == null)
            {
                return fallback;
            }

            try
            {
                return result.Value.ToObject<T>();
            }
            catch
            {
                return fallback;
            }

        }

        /// <summary>
        /// Installs a refreshed feature set unless a later refresh has already published one.
        /// </summary>
        private void PublishFeatures(IDictionary<string, Feature> features, long sequence)
        {
            if (features == null)
            {
                return;
            }

            lock (_publishLock)
            {
                if (sequence <= _publishedRefresh)
                {
                    return;
                }

                _publishedRefresh = sequence;
                _currentFeatures = features;
            }
        }

        private EvaluationContext BuildEvaluationContext(UserContext userContext, Experiment experiment = null)
        {
            var stickyBucketService = userContext?.StickyBucketService ?? _options.StickyBucketService;
            var stickyBucketDocs = userContext?.StickyBucketAssignmentDocs;
            var features = _currentFeatures;

            var global = new GlobalContext
            {
                Features = features,
                Enabled = _options.Enabled,
                QaMode = _options.QaMode,
                TrackingCallback = _options.TrackingCallback,
                StickyBucketService = stickyBucketService,
                ForcedVariations = _options.GlobalForcedVariations,
                ForcedFeatureValues = _options.GlobalForcedFeatureValues,
                Attributes = _options.GlobalAttributes
            };

            var user = new UserContext
            {
                Attributes = userContext?.Attributes ?? new JObject(),
                AttributeOverrides = userContext?.AttributeOverrides,
                ForcedVariations = userContext?.ForcedVariations,
                ForcedFeatureValues = userContext?.ForcedFeatureValues,
                TrackingCallback = userContext?.TrackingCallback,
                StickyBucketService = stickyBucketService,
                Url = userContext?.Url,
                StickyBucketAssignmentDocs = stickyBucketDocs ?? new Dictionary<string, StickyAssignmentsDocument>()
            };

            var context = new EvaluationContext(global, user);

            if (stickyBucketService != null && stickyBucketDocs == null)
            {
                // Resolved from the same merged attributes the evaluation hashes on, not from the user
                // attributes alone: an identifier can come from the global set or from an override, and
                // one looked up under the wrong value finds no document and re-buckets the user.
                List<Experiment> experiments = null;

                if (experiment != null)
                {
                    // An inline experiment is in no feature rule, so nothing else would name its identifier.
                    experiments = new List<Experiment> { experiment };
                }

                var attributeKeys = ExperimentUtilities.DeriveIdentifierAttributes(features, experiments, context.GetAttributes());
                var documents = stickyBucketService.GetAllAssignments(attributeKeys);

                if (documents != null)
                {
                    user.StickyBucketAssignmentDocs = documents;
                }
            }

            return context;
        }

        private static IGrowthBookFeatureRepository CreateRepository(Options options, ILoggerFactory loggerFactory, Action<bool> onFeaturesRefreshed)
        {
            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = options.ApiHost,
                ClientKey = options.ClientKey,
                DecryptionKey = options.DecryptionKey,
                CacheExpirationInSeconds = options.CacheExpirationInSeconds,
                PreferServerSentEvents = options.BackgroundSync,
                RequestHeaders = options.RequestHeaders,
                StreamingRequestHeaders = options.StreamingRequestHeaders,
                OnStreamingEventId = options.OnStreamingEventId,
                OnFeaturesRefreshed = onFeaturesRefreshed
            };

            var cache = options.FeatureCache ?? new InMemoryFeatureCache(cacheExpirationInSeconds: options.CacheExpirationInSeconds);
            var httpClientFactory = new HttpClientFactory(requestTimeoutInSeconds: options.HttpRequestTimeoutInSeconds);
            var workLogger = loggerFactory.CreateLogger<FeatureRefreshWorker>();
            var repoLogger = loggerFactory.CreateLogger<FeatureRepository>();
            var worker = new FeatureRefreshWorker(workLogger, httpClientFactory, config, cache);

            return new FeatureRepository(repoLogger, cache, worker);
        }
    }
}
