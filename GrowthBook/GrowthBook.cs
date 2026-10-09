using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using GrowthBook.Extensions;
using GrowthBook.Providers;
using GrowthBook.Services;
using GrowthBook.Utilities;
using GrowthBook.Exceptions;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// This is the C# client library for GrowthBook, the open-source
    //  feature flagging and A/B testing platform.
    //  More info at https://www.growthbook.io
    /// </summary>
    public class GrowthBook : IGrowthBook, IDisposable
    {
        private readonly bool _qaMode;
        private readonly Dictionary<string, ExperimentAssignment> _assigned;
        private readonly object _assignedLock = new object();
        private readonly ConcurrentDictionary<string, byte> _tracked;
        private Action<Experiment, ExperimentResult> _trackingCallback;
        private bool _disposedValue;
        private readonly IConditionEvaluationProvider _conditionEvaluator;
        private readonly FeatureEvaluationProvider _featureEvaluator;
        private readonly ExperimentEvaluationProvider _experimentEvaluator;
        private readonly IGrowthBookFeatureRepository _featureRepository;
        private readonly IStickyBucketService _stickyBucketService;
        private readonly ConcurrentDictionary<string, StickyAssignmentsDocument> _stickyBucketAssignmentDocs;
        private readonly ILogger<GrowthBook> _logger;
        private readonly JObject _savedGroups;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;
        private readonly bool _ownsFeatureRepository;
        private readonly Context _context;

        /// <summary>
        /// Whether there is anywhere to fetch features from at all.
        /// </summary>
        private readonly bool _canFetchFeatures;
        private JObject _previousAttributes;
        private IDictionary<string, int> _previousForcedVariations;

        /// <summary>
        /// Forces specific feature values regardless of evaluation rules. Overrides defaultValue, force rules, and experiments.
        /// </summary>
        public IDictionary<string, JToken> ForcedFeatureValues { get; set; }

        private readonly List<Action<Experiment, ExperimentResult>> _subscribers
            = new List<Action<Experiment, ExperimentResult>>();

        private readonly List<Func<Experiment, ExperimentResult, Task>> _asyncSubscribers
            = new List<Func<Experiment, ExperimentResult, Task>>();

        /// <summary>
        /// Creates a new GrowthBook instance from the passed context.
        /// </summary>
        /// <param name="context">The GrowthBook Context object.</param>
        public GrowthBook(Context context)
        {
            ValidateRemoteEvaluationConfiguration(context);

            _context = context;
            _canFetchFeatures = context.FeatureRepository != null || !string.IsNullOrWhiteSpace(context.ClientKey);
            Enabled = context.Enabled;
            Attributes = context.Attributes;
            Url = context.Url;
            Features = context.Features?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, Feature>();
            Experiments = context.Experiments ?? new List<Experiment>();
            ForcedVariations = context.ForcedVariations ?? new Dictionary<string, int>();

            _qaMode = context.QaMode;
            _trackingCallback = context.TrackingCallback;
            _assigned = new Dictionary<string, ExperimentAssignment>();
            _tracked = new ConcurrentDictionary<string, byte>();
            _stickyBucketService = context.StickyBucketService;
            _stickyBucketAssignmentDocs = context.StickyBucketAssignmentDocs != null
                ? new ConcurrentDictionary<string, StickyAssignmentsDocument>(context.StickyBucketAssignmentDocs)
                : new ConcurrentDictionary<string, StickyAssignmentsDocument>();
            _savedGroups = context.SavedGroups;
            _previousAttributes = context.Attributes?.DeepClone() as JObject;
            _previousForcedVariations = context.ForcedVariations?.ToDictionary(k => k.Key, v => v.Value);


            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = context.ApiHost ?? "https://cdn.growthbook.io",
                CacheExpirationInSeconds = context.CacheExpirationInSeconds,
                ClientKey = context.ClientKey,
                DecryptionKey = context.DecryptionKey,
                PreferServerSentEvents = context.BackgroundSync
            };

            // Map optional headers and callbacks
            if (context.RequestHeaders != null && context.RequestHeaders.Count > 0)
            {
                foreach (var kv in context.RequestHeaders)
                {
                    config.RequestHeaders[kv.Key] = kv.Value;
                }
            }

            if (context.StreamingRequestHeaders != null && context.StreamingRequestHeaders.Count > 0)
            {
                foreach (var kv in context.StreamingRequestHeaders)
                {
                    config.StreamingRequestHeaders[kv.Key] = kv.Value;
                }
            }

            config.OnFeaturesRefreshed = context.OnFeaturesRefreshed;
            config.OnStreamingEventId = context.OnStreamingEventId;

            // If they didn't want to include a logger factory, just create a basic one that will
            // create disabled loggers by default so we don't force a particular logging provider
            // or logs on the user if they chose the defaults.

            if (context.LoggerFactory != null)
            {
                _loggerFactory = context.LoggerFactory;
                _ownsLoggerFactory = false;
            }
            else
            {
                _loggerFactory = LoggerFactory.Create(builder => { });
                _ownsLoggerFactory = true;
            }

            _logger = _loggerFactory.CreateLogger<GrowthBook>();
            var conditionEvaluatorLogger = _loggerFactory.CreateLogger<ConditionEvaluationProvider>();

            _conditionEvaluator = new ConditionEvaluationProvider(conditionEvaluatorLogger);
            _experimentEvaluator = new ExperimentEvaluationProvider(
                _loggerFactory.CreateLogger<ExperimentEvaluationProvider>(),
                _conditionEvaluator);
            _featureEvaluator = new FeatureEvaluationProvider(
                _loggerFactory.CreateLogger<FeatureEvaluationProvider>(),
                _conditionEvaluator);

            ApplyEncryptedFeatures(context);

            if (context.FeatureRepository != null)
            {
                _featureRepository = context.FeatureRepository;
                _ownsFeatureRepository = false;
            }
            else
            {
                var featureCache = context.FeatureCache ??
                                   new InMemoryFeatureCache(cacheExpirationInSeconds: context.CacheExpirationInSeconds);
                var httpClientFactory =
                    new HttpClientFactory(requestTimeoutInSeconds: context.HttpRequestTimeoutInSeconds);
                _ownsFeatureRepository = true;

                var featureRefreshLogger = _loggerFactory.CreateLogger<FeatureRefreshWorker>();
                var featureRepositoryLogger = _loggerFactory.CreateLogger<FeatureRepository>();

                var featureRefreshWorker =
                    new FeatureRefreshWorker(featureRefreshLogger, httpClientFactory, config, featureCache);

                IRemoteEvaluationService remoteEvaluationService = null;
                if (context.RemoteEval)
                {
                    var remoteEvaluationLogger = _loggerFactory.CreateLogger<RemoteEvaluationService>();
                    remoteEvaluationService = new RemoteEvaluationService(remoteEvaluationLogger, httpClientFactory);
                }

                _featureRepository = new FeatureRepository(featureRepositoryLogger, featureCache, featureRefreshWorker,
                    remoteEvaluationService);
                _ownsFeatureRepository = true;
            }

            ForcedFeatureValues = context.ForcedFeatureValues;

            RefreshStickyBuckets();
        }

        private void ApplyEncryptedFeatures(Context context)
        {
            if (string.IsNullOrWhiteSpace(context.EncryptedFeatures))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(context.DecryptionKey))
            {
                throw new ArgumentException($"{nameof(Context.EncryptedFeatures)} was supplied without a {nameof(Context.DecryptionKey)} to decrypt it with", nameof(context));
            }

            _logger.LogInformation("Context contained encrypted features, decrypting them now");

            var decryptedFeaturesJson = context.EncryptedFeatures.DecryptWith(context.DecryptionKey);

            try
            {
                Features = JObject.Parse(decryptedFeaturesJson).ToObject<Dictionary<string, Feature>>()
                    ?? new Dictionary<string, Feature>();
            }
            catch (Exception ex)
            {
                throw new DecryptionException("The decrypted value was not a valid feature payload", ex);
            }

            _logger.LogInformation("Decrypted '{FeatureCount}' feature(s) from the context", Features.Count);
        }

        /// <summary>
        /// Arbitrary JSON object containing user and request attributes.
        /// </summary>
        public JObject Attributes { get; set; }

        /// <summary>
        /// Dictionary of the currently loaded feature objects.
        /// </summary>
        public IDictionary<string, Feature> Features { get; set; }

        /// <summary>
        /// The currently loaded experiments (separate from features).
        /// </summary>
        public IList<Experiment> Experiments { get; set; }

        /// <summary>
        /// Listing of specific experiments to always assign a specific variation (used for QA).
        /// </summary>
        public IDictionary<string, int> ForcedVariations { get; set; }

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        ///  Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Helper function used to cleanup object state.
        /// </summary>
        /// <param name="disposing">If true, dispose of large objects.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Attributes = null;
                    Features.Clear();
                    ForcedVariations = null;
                    _trackingCallback = null;
                    lock (_assignedLock)
                    {
                        _assigned.Clear();
                    }

                    _tracked.Clear();
                    _subscribers.Clear();
                    _asyncSubscribers.Clear();
                    if (_ownsFeatureRepository)
                    {
                        _featureRepository.Cancel();
                    }

                    if (_ownsLoggerFactory && _loggerFactory is IDisposable disposableFactory)
                    {
                        disposableFactory.Dispose();
                    }
                }

                _disposedValue = true;
            }
        }

        /// <summary>
        /// Called to dispose of this object's data.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// GrowthBook function to dispose of object data. Alias for Dispose().
        /// </summary>
        public void Destroy()
        {
            Dispose();
        }

        /// <summary>
        /// Updates user attributes from an IDictionary for singleton usage pattern.
        /// </summary>
        /// <param name="attributes">New user attributes as IDictionary</param>
        public void UpdateAttributes(IDictionary<string, object> attributes)
        {
            var newAttributes = attributes != null ? JObject.FromObject(attributes) : new JObject();

            if (_context.RemoteEval && ShouldTriggerRemoteEvaluation(newAttributes))
            {
                TriggerRemoteEvaluationAsync(newAttributes).ConfigureAwait(false);
            }

            Attributes = newAttributes;
            _previousAttributes = newAttributes?.DeepClone() as JObject;

            if (attributes != null)
            {
                Attributes = JObject.FromObject(attributes);
                _logger?.LogDebug("Updated attributes with {Count} properties", attributes.Count);
            }
            else
            {
                Attributes = new JObject();
                _logger?.LogDebug("Cleared attributes");
            }

            RefreshStickyBuckets();
        }

        /// <summary>
        /// Updates user attributes from an anonymous object for singleton usage pattern.
        /// </summary>
        /// <param name="attributes">New user attributes as anonymous object</param>
        public void UpdateAttributes(object attributes)
        {
            var newAttributes = attributes != null ? JObject.FromObject(attributes) : new JObject();

            if (_context.RemoteEval && ShouldTriggerRemoteEvaluation(newAttributes))
            {
                TriggerRemoteEvaluationAsync(newAttributes).ConfigureAwait(false);
            }

            Attributes = newAttributes;
            _previousAttributes = newAttributes?.DeepClone() as JObject;

            if (attributes != null)
            {
                Attributes = JObject.FromObject(attributes);
                _logger?.LogDebug("Updated attributes from object");
            }
            else
            {
                Attributes = new JObject();
                _logger?.LogDebug("Cleared attributes");
            }

            RefreshStickyBuckets();
        }

        /// <summary>
        /// Merges additional attributes with existing ones.
        /// </summary>
        /// <param name="additionalAttributes">Additional attributes to merge</param>
        public void MergeAttributes(IDictionary<string, object> additionalAttributes)
        {
            if (additionalAttributes == null) return;
            var oldAttributes = Attributes?.DeepClone() as JObject;


            foreach (var kvp in additionalAttributes)
            {
                Attributes[kvp.Key] = JToken.FromObject(kvp.Value);
            }

            if (_context.RemoteEval && ShouldTriggerRemoteEvaluation(Attributes))
            {
                TriggerRemoteEvaluationAsync(Attributes).ConfigureAwait(false);
            }

            _previousAttributes = Attributes?.DeepClone() as JObject;
            _logger?.LogDebug("Merged {Count} additional attributes", additionalAttributes.Count);

            RefreshStickyBuckets();
        }

        /// <summary>
        /// Merges additional attributes from an anonymous object with existing ones.
        /// </summary>
        /// <param name="additionalAttributes">Additional attributes to merge as anonymous object</param>
        public void MergeAttributes(object additionalAttributes)
        {
            if (additionalAttributes == null) return;

            var oldAttributes = Attributes?.DeepClone() as JObject;

            var additionalJObject = JObject.FromObject(additionalAttributes);
            foreach (var property in additionalJObject.Properties())
            {
                Attributes[property.Name] = property.Value;
            }

            if (_context.RemoteEval && ShouldTriggerRemoteEvaluation(Attributes))
            {
                TriggerRemoteEvaluationAsync(Attributes).ConfigureAwait(false);
            }

            _previousAttributes = Attributes?.DeepClone() as JObject;
            _logger?.LogDebug("Merged additional attributes from object");

            RefreshStickyBuckets();
        }

        /// <inheritdoc />
        public bool IsOn(string key)
        {
            return EvalFeature(key).On;
        }

        /// <inheritdoc />
        public bool IsOff(string key)
        {
            return EvalFeature(key).Off;
        }

        /// <summary>
        /// Asynchronously checks whether the specified feature is enabled.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns><c>true</c> if the feature is on; otherwise, <c>false</c>.</returns>
        public async Task<bool> IsOnAsync(string key, CancellationToken? cancellationToken = null)
        {
            await EnsureFeaturesLoaded(cancellationToken);
            var result = EvaluateFeature(key);
            var value = result.Value;
            return !value.IsNull() && value.ToObject<bool>();
        }

        /// <summary>
        /// Asynchronously checks whether the specified feature is disabled.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns><c>true</c> if the feature is off; otherwise, <c>false</c>.</returns>
        public async Task<bool> IsOffAsync(string key, CancellationToken? cancellationToken = null)
        {
            var on = await IsOnAsync(key, cancellationToken);
            return !on;
        }

        /// <summary>
        /// Subscribes a synchronous callback to experiment/feature evaluations.
        /// </summary>
        public IDisposable Subscribe(Action<Experiment, ExperimentResult> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _subscribers.Add(callback);
            return new Subscription(() => _subscribers.Remove(callback));
        }

        /// <summary>
        /// Subscribes an asynchronous callback to experiment/feature evaluations.
        /// </summary>
        public IDisposable SubscribeAsync(Func<Experiment, ExperimentResult, Task> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _asyncSubscribers.Add(callback);
            return new Subscription(() => _asyncSubscribers.Remove(callback));
        }

        private class Subscription : IDisposable
        {
            private readonly Action _unsubscribe;
            private bool _disposed;

            public Subscription(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _unsubscribe();
                    _disposed = true;
                }
            }
        }

        /// <inheritdoc />
        public T GetFeatureValue<T>(string key, T fallback, bool alwaysLoadFeatures = false)
        {
            // Keep the sync API, but avoid deadlocks by doing a quick synchronous spin only if already completed.
            // Prefer callers to use the async APIs.
            if (alwaysLoadFeatures)
            {
                // Fire-and-wait carefully to avoid deadlocks.
                LoadFeatures().GetAwaiter().GetResult();
            }

            var result = EvaluateFeature(key);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public async Task<T> GetFeatureValueAsync<T>(string key, T fallback,
            CancellationToken? cancellationToken = null)
        {
            var result = await EvalFeatureAsync(key, cancellationToken);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public IDictionary<string, ExperimentAssignment> GetAllResults()
        {
            lock (_assignedLock)
            {
                return new Dictionary<string, ExperimentAssignment>(_assigned);
            }
        }

        /// <inheritdoc />
        public FeatureResult EvalFeature(string featureId, bool alwaysLoadFeatures = false)
        {
            if (alwaysLoadFeatures)
            {
                LoadFeatures().GetAwaiter().GetResult();
            }

            return EvaluateFeature(featureId);
        }

        public async Task<FeatureResult> EvalFeatureAsync(string featureId, CancellationToken? cancellationToken = null)
        {
            await EnsureFeaturesLoaded(cancellationToken);

            return EvaluateFeature(featureId);
        }

        /// <summary>
        /// Loads the features these methods evaluate against, unless there is nowhere to load them from.
        /// A payload supplied on the <see cref="Context"/> - plaintext or encrypted - with no client key
        /// and no repository of the caller's own is a deliberately offline setup: fetching would call an
        /// API that was never configured, and a response that did arrive would replace the payload the
        /// caller supplied.
        /// </summary>
        private async Task EnsureFeaturesLoaded(CancellationToken? cancellationToken)
        {
            if (!_canFetchFeatures)
            {
                _logger.LogDebug("No client key and no supplied repository, evaluating the features the context carried");

                return;
            }

            await LoadFeatures(cancellationToken: cancellationToken);
        }

        private FeatureResult EvaluateFeature(string featureId, ISet<string> evaluatedFeatures = default)
        {
            var context = BuildEvaluationContext();
            return _featureEvaluator.EvaluateFeature(featureId, context);
        }

        /// <inheritdoc />
        public ExperimentResult Run(Experiment experiment)
        {
            try
            {
                var context = BuildEvaluationContext();
                var result = _experimentEvaluator.RunExperiment(experiment, null, context);
                TryAssignExperimentResult(experiment, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Encountered an unhandled exception while executing '{nameof(Run)}'");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task LoadFeatures(GrowthBookRetrievalOptions options = null,
            CancellationToken? cancellationToken = null)
        {
            var result = await LoadFeaturesWithResult(options, cancellationToken);

            if (!result.Success)
            {
                // For backward compatibility, we still throw exceptions in the original LoadFeatures method
                // Users who want better error handling should use LoadFeaturesWithResult
                throw result.Exception ?? new GrowthBookException(result.ErrorMessage);
            }
        }

        /// <inheritdoc />
        public async Task<FeatureLoadResult> LoadFeaturesWithResult(GrowthBookRetrievalOptions options = null,
            CancellationToken? cancellationToken = null)
        {
            try
            {
                _logger.LogInformation("Loading features from the repository");
                IDictionary<string, Feature> features;

                // Use remote evaluation if enabled and configured
                if (_context.RemoteEval && RemoteEvaluationUtilities.IsValidForRemoteEvaluation(_context))
                {
                    var currentContext = CreateCurrentContext();
                    features = await _featureRepository.GetFeaturesWithContext(currentContext, options,
                        cancellationToken);
                }
                else
                {
                    features = await _featureRepository.GetFeatures(options, cancellationToken);
                }

                if (features == null)
                {
                    var errorMessage = "Feature repository returned null - no features were loaded";
                    _logger.LogWarning(errorMessage);
                    return FeatureLoadResult.CreateFailure(errorMessage);
                }

                Features = features;
                RefreshStickyBuckets();
                var featureCount = Features.Count;

                _logger.LogInformation($"Loading features has completed, retrieved '{featureCount}' features");

                return FeatureLoadResult.CreateSuccess(featureCount);
            }
            catch (FeatureLoadException ex)
            {
                var errorMessage = $"Failed to load features: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Keep Features as is (don't set to null) to avoid NullReferenceExceptions
                return FeatureLoadResult.CreateFailure(errorMessage, ex, ex.StatusCode);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Encountered an unhandled exception while loading features: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Keep Features as is (don't set to null) to avoid NullReferenceExceptions
                return FeatureLoadResult.CreateFailure(errorMessage, ex);
            }
        }

        private void RefreshStickyBuckets()
        {
            if (_stickyBucketService == null) return;

            var formattedKeys = ExperimentUtilities.DeriveIdentifierAttributes(Features, Experiments, Attributes);
            var docs = _stickyBucketService.GetAllAssignments(formattedKeys);

            foreach (var kvp in docs)
            {
                _stickyBucketAssignmentDocs[kvp.Key] = kvp.Value;
            }
        }

        private void TryAssignExperimentResult(Experiment experiment, ExperimentResult result)
        {
            if (experiment == null)
            {
                NotifySubscribers(null, result);

                return;
            }

            var assignment = new ExperimentAssignment { Experiment = experiment, Result = result };
            bool shouldFireCallbacks = false;

            lock (_assignedLock)
            {
                // Always record the assignment locally for GetAllResults()
                if (!_assigned.TryGetValue(experiment.Key, out ExperimentAssignment prev)
                    || prev.Result.InExperiment != result.InExperiment
                    || prev.Result.VariationId != result.VariationId)
                {
                    _assigned[experiment.Key] = assignment;
                    shouldFireCallbacks = true;
                }
            }

            // Also use repository tracking if available (for preventing duplicate callbacks across instances)
            if (_featureRepository != null)
            {
                if (!_featureRepository.HasIdenticalAssignment(experiment.Key, assignment))
                {
                    _featureRepository.RecordAssignment(experiment.Key, assignment);
                }
            }

            // Fire subscription callbacks if needed
            if (shouldFireCallbacks)
            {
                NotifySubscribers(experiment, result);
            }
        }

        /// <summary>
        /// Calls the tracking callback function to track experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was assigned.</param>
        /// <param name="result">The result of the assignment.</param>
        private void TryToTrack(Experiment experiment, ExperimentResult result)
        {
            if (_trackingCallback == null)
            {
                return;
            }

            string key = result.HashAttribute + result.HashValue + experiment.Key + result.VariationId;

            // Use atomic operations to prevent race conditions in concurrent scenarios
            bool shouldTrack = false;

            if (_featureRepository != null)
            {
                // TryMarkAsTracked returns true only if key was successfully added (didn't exist before)
                shouldTrack = _featureRepository.TryMarkAsTracked(key);
            }
            else
            {
                // TryAdd returns true only if key was successfully added (didn't exist before)
                shouldTrack = _tracked.TryAdd(key, 0);
            }

            if (shouldTrack)
            {
                try
                {
                    _trackingCallback(experiment, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Encountered unhandled exception during tracking callback for experiment with combined key \'{Key}\'",
                        key);
                }
            }
        }

        /// <summary>
        /// Validates that remote evaluation configuration is correct.
        /// </summary>
        /// <param name="context">The context to validate</param>
        private static void ValidateRemoteEvaluationConfiguration(Context context)
        {
            if (!context.RemoteEval) return;

            if (string.IsNullOrWhiteSpace(context.ClientKey))
            {
                throw new ArgumentException("ClientKey is required when RemoteEval is enabled", nameof(context));
            }

            if (string.IsNullOrWhiteSpace(context.ApiHost))
            {
                throw new ArgumentException("ApiHost is required when RemoteEval is enabled", nameof(context));
            }

            if (!string.IsNullOrWhiteSpace(context.DecryptionKey))
            {
                throw new ArgumentException(
                    "RemoteEval cannot be used with DecryptionKey - features are evaluated server-side",
                    nameof(context));
            }
        }

        /// <summary>
        /// Determines if remote evaluation should be triggered based on attribute or forced variation changes.
        /// </summary>
        /// <param name="newAttributes">The new attributes to check</param>
        /// <returns>True if remote evaluation should be triggered</returns>
        private bool ShouldTriggerRemoteEvaluation(JObject newAttributes)
        {
            // Check if attributes changed
            var attributesChanged = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluation(
                _previousAttributes,
                newAttributes,
                _context.CacheKeyAttributes
            );

            // Check if forced variations changed
            var forcedVariationsChanged = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluationForForcedVariations(
                _previousForcedVariations,
                ForcedVariations
            );

            return attributesChanged || forcedVariationsChanged;
        }

        /// <summary>
        /// Triggers remote evaluation asynchronously when attribute changes are detected.
        /// </summary>
        /// <param name="newAttributes">The new attributes</param>
        private async Task TriggerRemoteEvaluationAsync(JObject newAttributes)
        {
            try
            {
                _logger?.LogDebug("Triggering remote evaluation due to attribute changes");

                var currentContext = CreateCurrentContext();
                var features = await _featureRepository.GetFeaturesWithContext(currentContext);

                if (features != null)
                {
                    Features = features;
                    _logger?.LogDebug("Remote evaluation completed, updated {Count} features", features.Count);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to trigger remote evaluation, continuing with cached features");
            }
        }

        /// <summary>
        /// Creates a context object with current state for remote evaluation.
        /// </summary>
        /// <returns>A context object with current state</returns>
        private Context CreateCurrentContext()
        {
            return new Context
            {
                RemoteEval = _context.RemoteEval,
                ApiHost = _context.ApiHost,
                ClientKey = _context.ClientKey,
                CacheKeyAttributes = _context.CacheKeyAttributes,
                Attributes = Attributes,
                ForcedVariations = ForcedVariations,
                Url = Url
            };
        }


        /// <summary>
        /// Notifies all synchronous and asynchronous subscribers about a feature or experiment evaluation result.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated (null if feature evaluation).</param>
        /// <param name="result">The result of the evaluation.</param>
        private void NotifySubscribers(Experiment experiment, ExperimentResult result)
        {
            foreach (var subscriber in _subscribers)
            {
                try
                {
                    subscriber(experiment, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Encountered unhandled exception in synchronous subscriber.");
                }
            }

            foreach (var asyncSubscriber in _asyncSubscribers)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await asyncSubscriber(experiment, result).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Encountered unhandled exception in asynchronous subscriber.");
                    }
                });
            }
        }

        private EvaluationContext BuildEvaluationContext()
        {
            var global = new GlobalContext
            {
                Features = Features,
                SavedGroups = _savedGroups,
                Experiments = Experiments,
                Enabled = Enabled,
                QaMode = _qaMode,
                ForcedVariations = ForcedVariations,
                TrackingCallback = (exp, res) => TryToTrack(exp, res),
                OnExperimentEval = (exp, res) => TryAssignExperimentResult(exp, res),
                StickyBucketService = _stickyBucketService,
                ForcedFeatureValues = ForcedFeatureValues
            };

            var user = new UserContext
            {
                Attributes = Attributes,
                StickyBucketAssignmentDocs = _stickyBucketAssignmentDocs,
                ForcedVariations = null,
                Url = Url,
                ForcedFeatureValues = ForcedFeatureValues
            };

            return new EvaluationContext(global, user);
        }
    }
}
