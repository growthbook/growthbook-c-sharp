using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using GrowthBook.Evaluation;
using GrowthBook.Extensions;
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
        private readonly ILogger<GrowthBookClient> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;
        private readonly bool _ownsRepository;
        private readonly IDisposable _refreshSubscription;
        private volatile IDictionary<string, Feature> _currentFeatures = new Dictionary<string, Feature>();

        /// <summary>
        /// Orders the feature sets. Refreshes overlap, and one that started earlier carries older
        /// definitions however late it comes back.
        /// </summary>
        private long _refreshSequence;
        private long _publishedRefresh;
        private readonly object _publishLock = new object();
        private long _installedRefreshVersion;
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

            _logger = _loggerFactory.CreateLogger<GrowthBookClient>();

            _conditionEvaluator = new ConditionEvaluationProvider(_loggerFactory.CreateLogger<ConditionEvaluationProvider>());
            // Built as a pair and wired to each other: a feature rule can run an experiment and an
            // experiment's prerequisite is a feature, so neither works alone.
            _experimentEvaluator = new ExperimentEvaluationProvider(
                _loggerFactory.CreateLogger<ExperimentEvaluationProvider>(), _conditionEvaluator);
            _featureEvaluator = new FeatureEvaluationProvider(
                _loggerFactory.CreateLogger<FeatureEvaluationProvider>(), _conditionEvaluator);

            _featureEvaluator.ExperimentEvaluator = _experimentEvaluator;
            _experimentEvaluator.FeatureEvaluator = _featureEvaluator;

            if (options.FeatureRepository != null)
            {
                _repository = options.FeatureRepository;
                _ownsRepository = false;
            }
            else
            {
                _repository = CreateRepository(options, _loggerFactory);
                _ownsRepository = true;
            }

            if (_repository is IFeatureRefreshSource refreshSource)
            {
                _refreshSubscription = refreshSource.SubscribeToRefresh(InstallRefresh);
            }
        }

        /// <summary>
        /// Loads features from the repository. Call once at application startup before serving requests.
        /// </summary>
        /// <returns>
        /// Whether features were loaded. A repository that answers without any - such as an API response with no
        /// <c>features</c> - is a failed load, not an empty one.
        /// </returns>
        public async Task<FeatureLoadResult> InitializeAsync(CancellationToken ct = default)
        {
            var sequence = Interlocked.Increment(ref _refreshSequence);

            // A refresh started here is waited for, since nothing else installs its response. A repository of the
            // caller's own is asked as before.
            var retrievalOptions = _ownsRepository ? new GrowthBookRetrievalOptions { WaitForCompletion = true } : null;
            var features = await _repository.GetFeatures(retrievalOptions, ct);

            if (features == null)
            {
                return FeatureLoadResult.CreateFailure("The feature repository returned no features");
            }

            PublishFeatures(features, sequence);

            return FeatureLoadResult.CreateSuccess(features.Count);
        }

        /// <summary>
        /// Forces a refresh of features from the API, bypassing the cache.
        /// </summary>
        /// <returns>
        /// Whether features were loaded, on the same terms as <see cref="InitializeAsync"/>. A failed refresh leaves
        /// the features already in use in place.
        /// </returns>
        public async Task<FeatureLoadResult> RefreshFeaturesAsync(CancellationToken ct = default)
        {
            var sequence = Interlocked.Increment(ref _refreshSequence);
            var features = await _repository.GetFeatures(
                new GrowthBookRetrievalOptions { ForceRefresh = true, WaitForCompletion = true}, ct);

            if (features == null)
            {
                return FeatureLoadResult.CreateFailure("The feature repository returned no features");
            }

            PublishFeatures(features, sequence);

            return FeatureLoadResult.CreateSuccess(features.Count);
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
        /// Replaces the saved group definitions that <c>$inGroup</c> and <c>$notInGroup</c> conditions
        /// are evaluated against.
        /// </summary>
        public void SetSavedGroups(JObject savedGroups)
            => _options.SavedGroups = savedGroups;

        /// <summary>
        /// Replaces the tracking callback used to report experiment assignments to your analytics system.
        /// </summary>
        public void SetTrackingCallback(Action<Experiment, ExperimentResult> callback)
            => _options.TrackingCallback = callback;

        /// <summary>Returns the current global attributes, or null if none are set.</summary>
        public JObject GetGlobalAttributes()
            => _options.GlobalAttributes;

        /// <summary>
        /// Returns a snapshot of the currently loaded features. The map is a copy, so adding or removing
        /// entries cannot change what every other user in the process evaluates against. The
        /// <see cref="Feature"/> objects in it are the live ones, however - treat them as read only.
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

            _refreshSubscription?.Dispose();

            if (_ownsRepository)
            {
                _repository.Cancel();

                if (_repository is IDisposable disposableRepository)
                {
                    disposableRepository.Dispose();
                }
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
            if (result.Value.IsNull())
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

        /// <summary>
        /// Installs a refresh the repository relayed. One that overtook a newer one on the way is dropped by its
        /// version, and the rest are sequenced as they are installed, under the same lock, so a load started
        /// earlier and answered later cannot replace them.
        /// </summary>
        private void InstallRefresh(FeatureRefresh refresh)
        {
            if (refresh?.Features == null)
            {
                return;
            }

            lock (_publishLock)
            {
                if (refresh.Version <= _installedRefreshVersion)
                {
                    return;
                }

                _installedRefreshVersion = refresh.Version;

                PublishFeatures(refresh.Features, Interlocked.Increment(ref _refreshSequence));
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
                Attributes = _options.GlobalAttributes,
                SavedGroups = _options.SavedGroups
            };

            var user = new UserContext
            {
                Attributes = userContext?.Attributes ?? new JObject(),
                AttributeOverrides = userContext?.AttributeOverrides,
                ForcedVariations = userContext?.ForcedVariations,
                TrackingCallback = userContext?.TrackingCallback,
                StickyBucketService = stickyBucketService,
                Url = userContext?.Url,
                StickyBucketAssignmentDocs = stickyBucketDocs ?? new Dictionary<string, StickyAssignmentsDocument>(),
                // Without a set carried by the caller, one evaluation's exposures are still deduplicated among
                // themselves - a feature that reaches the same experiment through several rules reports it once.
                TrackedExperiments = userContext?.TrackedExperiments ?? new HashSet<string>()
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

        private static IGrowthBookFeatureRepository CreateRepository(Options options, ILoggerFactory loggerFactory)
        {
            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = options.ApiHost,
                ClientKey = options.ClientKey,
                DecryptionKey = options.DecryptionKey,
                CacheExpirationInSeconds = options.CacheExpirationInSeconds,
                PreferServerSentEvents = options.BackgroundSync
            };

            var cache = new InMemoryFeatureCache(cacheExpirationInSeconds: options.CacheExpirationInSeconds);
            var httpClientFactory = options.HttpClientFactory ?? new HttpClientFactory(requestTimeoutInSeconds: options.HttpRequestTimeoutInSeconds);
            var workLogger = loggerFactory.CreateLogger<FeatureRefreshWorker>();
            var repoLogger = loggerFactory.CreateLogger<FeatureRepository>();
            var worker = new FeatureRefreshWorker(workLogger, httpClientFactory, config, cache);

            return new FeatureRepository(repoLogger, cache, worker);
        }
    }
}
