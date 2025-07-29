using System;
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
        private readonly HashSet<string> _tracked;
        private Action<Experiment, ExperimentResult> _trackingCallback;
        private readonly List<Action<Experiment, ExperimentResult>> _subscriptions;
        private bool _disposedValue;
        private readonly IConditionEvaluationProvider _conditionEvaluator;
        private readonly IGrowthBookFeatureRepository _featureRepository;
        private readonly IStickyBucketService _stickyBucketService;
        private readonly IDictionary<string, StickyAssignmentsDocument> _stickyBucketAssignmentDocs;
        private readonly ILogger<GrowthBook> _logger;
        private readonly JObject _savedGroups;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;

        /// <summary>
        /// Creates a new GrowthBook instance from the passed context.
        /// </summary>
        /// <param name="context">The GrowthBook Context object.</param>
        public GrowthBook(Context context)
        {
            Enabled = context.Enabled;
            Attributes = context.Attributes;
            Url = context.Url;
            Features = context.Features?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, Feature>();
            Experiments = context.Experiments ?? new List<Experiment>();
            ForcedVariations = context.ForcedVariations;

            _qaMode = context.QaMode;
            _trackingCallback = context.TrackingCallback;
            _tracked = new HashSet<string>();
            _assigned = new Dictionary<string, ExperimentAssignment>();
            _subscriptions = new List<Action<Experiment, ExperimentResult>>();
            _stickyBucketService = context.StickyBucketService;
            _stickyBucketAssignmentDocs = context.StickyBucketAssignmentDocs ?? new Dictionary<string, StickyAssignmentsDocument>();
            _savedGroups = context.SavedGroups;

            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = context.ApiHost ?? "https://cdn.growthbook.io",
                CacheExpirationInSeconds = 60,
                ClientKey = context.ClientKey,
                DecryptionKey = context.DecryptionKey,
                PreferServerSentEvents = true
            };

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

            if (context.FeatureRepository != null)
            {
                _featureRepository = context.FeatureRepository;
            }
            else
            {
                var httpClientFactory = new HttpClientFactory(requestTimeoutInSeconds: 60);

                // Use file-based cache (similar to Swift CachingManager)
                var featureCacheLogger = _loggerFactory.CreateLogger<Api.InMemoryFeatureCache>();
                var featureCache = new Api.InMemoryFeatureCache(logger: featureCacheLogger);
                
                if (!string.IsNullOrEmpty(context.CachePath))
                {
                    featureCache.SetCustomCachePath(context.CachePath);
                }
                
                if (!string.IsNullOrEmpty(context.ClientKey))
                {
                    featureCache.SetCacheKey(context.ClientKey);
                }
                
                var featureRefreshLogger = _loggerFactory.CreateLogger<FeatureRefreshWorker>();
                var featureRepositoryLogger = _loggerFactory.CreateLogger<FeatureRepository>();
                var featureRefreshWorker = new FeatureRefreshWorker(featureRefreshLogger, httpClientFactory, config, featureCache);
                
                _featureRepository = new FeatureRepository(featureRepositoryLogger, featureCache, featureRefreshWorker);
                
                _logger.LogDebug("GrowthBook initialized with file-based cache");
            }
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
                    _tracked.Clear();
                    _assigned.Clear();
                    _subscriptions.Clear();
                    _featureRepository.Cancel();

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

        /// <inheritdoc />
        public T GetFeatureValue<T>(string key, T fallback, bool alwaysLoadFeatures = false)
        {
            if (alwaysLoadFeatures)
            {
                LoadFeatures().Wait();
            }

            var result = EvaluateFeature(key);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public async Task<T> GetFeatureValueAsync<T>(string key, T fallback, CancellationToken? cancellationToken = null)
        {
            var result = await EvalFeatureAsync(key, cancellationToken);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public IDictionary<string, ExperimentAssignment> GetAllResults()
        {
            return _assigned;
        }


        /// <inheritdoc />
        public Action Subscribe(Action<Experiment, ExperimentResult> callback)
        {
            _subscriptions.Add(callback);
            return () => _subscriptions.Remove(callback);
        }

        /// <inheritdoc />
        public FeatureResult EvalFeature(string featureId, bool alwaysLoadFeatures = false)
        {
            if (alwaysLoadFeatures)
            {
                LoadFeatures().Wait();
            }

            return EvaluateFeature(featureId);
        }

        public async Task<FeatureResult> EvalFeatureAsync(string featureId, CancellationToken? cancellationToken = null)
        {
            await LoadFeatures(cancellationToken: cancellationToken);

            return EvaluateFeature(featureId);
        }

        private FeatureResult EvaluateFeature(string featureId, ISet<string> evaluatedFeatures = default)
        {
            try
            {
                evaluatedFeatures = evaluatedFeatures ?? new HashSet<string>();

                if (evaluatedFeatures.Contains(featureId))
                {
                    return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                }

                evaluatedFeatures.Add(featureId);

                if (!Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                foreach (FeatureRule rule in feature?.Rules ?? Enumerable.Empty<FeatureRule>())
                {
                    if (rule.ParentConditions != null)
                    {
                        var passedPrerequisiteEvaluations = true;

                        foreach (var parentCondition in rule.ParentConditions)
                        {
                            var parentResult = EvaluateFeature(parentCondition.Id, evaluatedFeatures);

                            // Don't continue evaluating if the prerequisite conditions have cycles.

                            if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                            {
                                return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                            }

                            var evaluationObject = new JObject { ["value"] = parentResult.Value };

                            var isSuccess = _conditionEvaluator.EvalCondition(evaluationObject, parentCondition.Condition ?? new JObject(), _savedGroups);

                            if (!isSuccess)
                            {
                                // When the parent evaluation is gated we'll treat that as a complete failure.

                                if (parentCondition.Gate)
                                {
                                    return GetFeatureResult(default, FeatureResult.SourceId.Prerequisite);
                                }

                                passedPrerequisiteEvaluations = false;
                                break;
                            }
                        }

                        if (!passedPrerequisiteEvaluations)
                        {
                            continue;
                        }
                    }

                    if (rule.Filters?.Any() == true && IsFilteredOut(rule.Filters))
                    {
                        continue;
                    }

                    if (!rule.Condition.IsNull() && !_conditionEvaluator.EvalCondition(Attributes, rule.Condition, _savedGroups))
                    {
                        continue;
                    }

                    if (!rule.Force.IsNull())
                    {
                        if (!IsIncludedInRollout(rule.Seed ?? featureId, rule.HashAttribute, rule.Range, rule.Coverage, rule.HashVersion))
                        {
                            continue;
                        }

                        if (_trackingCallback != null && rule.Tracks?.Any() == true)
                        {
                            foreach (var trackData in rule.Tracks)
                            {
                                try
                                {
                                    _trackingCallback?.Invoke(trackData.Experiment, trackData.Result);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"Encountered unhandled exception in tracking callback for feature ID '{featureId}'");
                                }
                            }
                        }

                        return GetFeatureResult(rule.Force, FeatureResult.SourceId.Force);
                    }

                    var experiment = new Experiment
                    {
                        Variations = rule.Variations,
                        Key = rule.Key ?? featureId,
                        Coverage = rule.Coverage,
                        Weights = rule.Weights,
                        HashAttribute = rule.HashAttribute,
                        FallbackAttribute = rule.FallbackAttribute,
                        DisableStickyBucketing = rule.DisableStickyBucketing,
                        BucketVersion = rule.BucketVersion,
                        MinBucketVersion = rule.MinBucketVersion,
                        Namespace = rule.Namespace,
                        Meta = rule.Meta,
                        Ranges = rule.Ranges,
                        Name = rule.Name,
                        Phase = rule.Phase,
                        Seed = rule.Seed,
                        Filters = rule.Filters,
                        HashVersion = rule.HashVersion,
                        Condition = rule.Condition
                    };

                    var result = RunExperiment(experiment, featureId);

                    TryAssignExperimentResult(experiment, result);

                    if (!result.InExperiment || result.Passthrough)
                    {
                        continue;
                    }

                    return GetFeatureResult(result.Value, FeatureResult.SourceId.Experiment, experiment, result);
                }

                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Encountered an unhandled exception while executing '{nameof(EvalFeature)}'");

                if (!Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
        }

        /// <inheritdoc />
        public ExperimentResult Run(Experiment experiment)
        {
            try
            {
                ExperimentResult result = RunExperiment(experiment, null);

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
        public async Task LoadFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
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
        public async Task<FeatureLoadResult> LoadFeaturesWithResult(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                _logger.LogInformation("Loading features from the repository");

                var features = await _featureRepository.GetFeatures(options, cancellationToken);

                if (features == null)
                {
                    var errorMessage = "Feature repository returned null - no features were loaded";
                    _logger.LogWarning(errorMessage);
                    return FeatureLoadResult.CreateFailure(errorMessage);
                }

                Features = features;
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

        private void TryAssignExperimentResult(Experiment experiment, ExperimentResult result)
        {
            if (!_assigned.TryGetValue(experiment.Key, out ExperimentAssignment prev)
                || prev.Result.InExperiment != result.InExperiment
                || prev.Result.VariationId != result.VariationId)
            {
                _assigned.Add(experiment.Key, new ExperimentAssignment { Experiment = experiment, Result = result });

                foreach (Action<Experiment, ExperimentResult> callback in _subscriptions)
                {
                    try
                    {
                        callback?.Invoke(experiment, result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Encountered exception during subscription callback for experiment with key '{experiment.Key}'");
                    }
                }
            }
        }

        private ExperimentResult RunExperiment(Experiment experiment, string featureId)
        {
            // 1. Abort if there aren't enough variations present.

            if (experiment.Variations.IsNull() || experiment.Variations.Count < 2)
            {
                _logger.LogDebug("Aborting experiment, not enough variations are present");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 2. Abort if GrowthBook is currently disabled.

            if (!Enabled)
            {
                _logger.LogDebug("Aborting experiment, GrowthBook is not currently enabled");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // NOTE: The improved URL targeting mentioned is only applicable on the front end.
            //       There are potential frontend usages for the C# SDK, but until there is more clarity and more robust tests
            //       in the JSON test suite to ensure we get an appropriate implementation in place we are going to hold off on this.

            // 2.6 Use improved URL targeting if specified.

            //if (experiment.UrlPatterns?.Count > 0 && !ExperimentUtilities.IsUrlTargeted(Url ?? string.Empty, experiment.UrlPatterns))
            //{
            //    _logger.LogDebug("Skipping due to URL targeting");
            //    return GetExperimentResult(experiment, featureId: featureId);
            //}

            // 3. Use the override value from the query string if one is specified.

            if (!Url.IsNullOrWhitespace())
            {
                var overrideValue = ExperimentUtilities.GetQueryStringOverride(experiment.Key, Url, experiment.Variations.Count);

                if (overrideValue != null)
                {
                    _logger.LogDebug("Found an override value in the query string, creating experiment result from it");
                    return GetExperimentResult(experiment, overrideValue.Value, featureId: featureId);
                }
            }

            // 4. Use the forced variation value instead if one is specified for this experiment.

            if (ForcedVariations.TryGetValue(experiment.Key, out var variation))
            {
                _logger.LogDebug("Found a forced variation value, creating experiment result from it");
                return GetExperimentResult(experiment, variation, featureId: featureId);
            }

            // 5. Abort if the experiment isn't currently active.

            if (!experiment.Active)
            {
                _logger.LogDebug("Aborting experiment, experiment is not currently active");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 6. Abort if we're unable to generate a hash identifying this run.

            (var hashAttribute, var hashValue) = Attributes.GetHashAttributeAndValue(experiment.HashAttribute);

            if (hashValue.IsNullOrWhitespace())
            {
                // Check if a fallback attribute for sticky bucketing exists and use it if possible.

                var hasFallback = !experiment.FallbackAttribute.IsNullOrWhitespace();

                if (hasFallback)
                {
                    (hashAttribute, hashValue) = Attributes.GetHashAttributeAndValue(experiment.FallbackAttribute);
                }
                else
                {
                    _logger.LogDebug("Aborting experiment, unable to locate a value for the experiment hash attribute \'{ExperimentHashAttribute}\'", experiment.HashAttribute);
                    return GetExperimentResult(experiment, featureId: featureId);
                }
            }

            // 6.5 When sticky bucketing is permitted, determine if they already have a value and use it if possible.

            var assignedBucket = -1;
            var foundStickyBucket = false;
            var stickyBucketVersionIsBlocked = false;

            if (_stickyBucketService != null && !experiment.DisableStickyBucketing)
            {
                var bucketVersion = experiment.BucketVersion;
                var minBucketVersion = experiment.MinBucketVersion;
                var meta = experiment.Meta ?? new List<VariationMeta>();

                var stickyBucketVariation = ExperimentUtilities.GetStickyBucketVariation(
                    experiment,
                    bucketVersion,
                    minBucketVersion,
                    meta,
                    Attributes,
                    _stickyBucketAssignmentDocs
                );

                foundStickyBucket = stickyBucketVariation.VariationIndex >= 0;
                assignedBucket = stickyBucketVariation.VariationIndex;
                stickyBucketVersionIsBlocked = stickyBucketVariation.IsVersionBlocked;
            }

            if (!foundStickyBucket)
            {
                // 7. Abort if this run is ineligible to be included in the experiment.

                if (experiment.Filters?.Any() == true)
                {
                    if (IsFilteredOut(experiment.Filters))
                    {
                        _logger.LogDebug("Aborting experiment, filters have been applied and matched this run");
                        return GetExperimentResult(experiment, featureId: featureId);
                    }
                }
                else if (experiment.Namespace != null && !ExperimentUtilities.InNamespace(hashValue, experiment.Namespace))
                {
                    _logger.LogDebug("Aborting experiment, not within the specified namespace \'{ExperimentNamespace}\'", experiment.Namespace);
                    return GetExperimentResult(experiment, featureId: featureId);
                }

                // 8. Abort if the conditions for the experiment prohibit this.

                if (!experiment.Condition.IsNull())
                {
                    if (!_conditionEvaluator.EvalCondition(Attributes, experiment.Condition, _savedGroups))
                    {
                        _logger.LogDebug("Aborting experiment, associated conditions have prohibited participation");
                        return GetExperimentResult(experiment, featureId: featureId);
                    }
                }

                if (experiment.ParentConditions != null)
                {
                    foreach (var parentCondition in experiment.ParentConditions)
                    {
                        var parentResult = EvaluateFeature(parentCondition.Id);

                        if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                        {
                            return GetExperimentResult(experiment, featureId: featureId);
                        }

                        var evaluationObject = new JObject { ["value"] = parentResult.Value };

                        if (!_conditionEvaluator.EvalCondition(evaluationObject, parentCondition.Condition ?? new JObject(), _savedGroups))
                        {
                            return GetExperimentResult(experiment, featureId: featureId);
                        }
                    }
                }
            }

            // 9. Attempt to assign this run to an experiment variation.

            var hash = HashUtilities.Hash(experiment.Seed ?? experiment.Key, hashValue, experiment.HashVersion);

            if (hash is null)
            {
                return GetExperimentResult(experiment, featureId: featureId);
            }

            if (!foundStickyBucket)
            {
                var ranges = experiment.Ranges?.Count > 0 ? experiment.Ranges : ExperimentUtilities.GetBucketRanges(experiment.Variations?.Count ?? 0, experiment.Coverage ?? 1, experiment.Weights ?? new List<double>());
                assignedBucket = ExperimentUtilities.ChooseVariation(hash.Value, ranges.ToList());

                // 10. Abort if a variation could not be assigned.

                if (assignedBucket == -1)
                {
                    _logger.LogDebug("Aborting experiment, unable to assign this run to an experiment variation");
                    return GetExperimentResult(experiment, featureId: featureId);
                }
            }

            // 9.5 Unenroll if any prior sticky buckets are blocked by version.

            if (stickyBucketVersionIsBlocked)
            {
                return GetExperimentResult(experiment, featureId: featureId, wasStickyBucketUsed: true);
            }

            // 11. Use the forced value for the experiment if one is specified.

            if (experiment.Force != null)
            {
                _logger.LogDebug("Found a forced value, creating experiment result from it");
                return GetExperimentResult(experiment, experiment.Force.Value, featureId: featureId);
            }

            // 12. Abort if we're currently operating in QA mode.

            if (_qaMode)
            {
                _logger.LogDebug("Aborting experiment, this run is in QA mode");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 13. Run the experiment and track the result if we haven't seen this one before.

            _logger.LogInformation("Participation in experiment with key \'{ExperimentKey}\' is allowed, running the experiment", experiment.Key);
            var result = GetExperimentResult(experiment, assignedBucket, true, featureId, hash, foundStickyBucket);

            // 13.5 Store the value for later if sticky bucketing is enabled.

            if (_stickyBucketService != null && !experiment.DisableStickyBucketing)
            {
                var experimentKey = ExperimentUtilities.GetStickyBucketExperimentKey(experiment.Key, experiment.BucketVersion);

                var assignments = new Dictionary<string, string>
                {
                    [experimentKey] = result.Key
                };

                (var document, var isChanged) = ExperimentUtilities.GenerateStickyBucketAssignment(_stickyBucketService, hashAttribute, hashValue, assignments);

                if (isChanged)
                {
                    _stickyBucketService.SaveAssignments(document);
                }
            }

            TryToTrack(experiment, result);

            return result;
        }

        private FeatureResult GetFeatureResult(JToken value, string source, Experiment experiment = null, ExperimentResult experimentResult = null)
        {
            return new FeatureResult
            {
                Value = value,
                Source = source,
                Experiment = experiment,
                ExperimentResult = experimentResult
            };
        }

        private bool IsFilteredOut(IEnumerable<Filter> filters)
        {
            foreach (var filter in filters)
            {
                (_, var hashValue) = Attributes.GetHashAttributeAndValue(filter.Attribute);

                if (hashValue.IsNullOrWhitespace())
                {
                    _logger.LogDebug("Attributes are missing a filter\'s hash attribute of \'{FilterAttribute}\', marking as filtered out", filter.Attribute);
                    return true;
                }

                var bucket = HashUtilities.Hash(filter.Seed, hashValue, filter.HashVersion);

                var isInAnyRange = filter.Ranges.Any(x => ExperimentUtilities.InRange(bucket.Value, x));

                if (!isInAnyRange)
                {
                    _logger.LogDebug("This run is not in any range associated with a filter, marking as filtered out");
                    return true;
                }
            }

            return false;
        }

        private bool IsIncludedInRollout(string seed, string hashAttribute = null, BucketRange range = null, double? coverage = null, int? hashVersion = null)
        {
            if (coverage == null && range == null)
            {
                _logger.LogDebug("No coverage value or range was specified, marking as included in rollout");
                return true;
            }

            if (range is null && coverage == 0)
            {
                _logger.LogDebug("Range and coverage were not set, marking as not included in rollout");
                return false;
            }

            (_, var hashValue) = Attributes.GetHashAttributeAndValue(hashAttribute);

            if (hashValue is null)
            {
                _logger.LogDebug("Attributes do not have a value for hash attribute \'{HashAttribute}\', marking as excluded from rollout", hashAttribute);
                return false;
            }

            var bucket = HashUtilities.Hash(seed, hashValue, hashVersion ?? 1);

            if (range != null)
            {
                return ExperimentUtilities.InRange(bucket.Value, range);
            }

            if (coverage != null)
            {
                return bucket <= coverage;
            }

            return true;
        }

        /// <summary>
        /// Generates an experiment result from an experiment.
        /// </summary>
        /// <param name="experiment">The experiment to get the result from.</param>
        /// <param name="variationIndex">The variation id, if specified.</param>
        /// <param name="hashUsed">Whether or not a hash was used in assignment.</param>
        /// <returns>The experiment result.</returns>
        private ExperimentResult GetExperimentResult(Experiment experiment, int variationIndex = -1, bool hashUsed = false, string featureId = null, double? bucketHash = null, bool wasStickyBucketUsed = false)
        {
            var inExperiment = true;

            if (variationIndex < 0 || variationIndex >= experiment.Variations.Count)
            {
                variationIndex = 0;
                inExperiment = false;
            }

            var canUseStickyBucketing = _stickyBucketService != null && !experiment.DisableStickyBucketing;
            var fallbackAttribute = canUseStickyBucketing ? experiment.FallbackAttribute : default;

            (var hashAttribute, var hashValue) = Attributes.GetHashAttributeAndValue(experiment.HashAttribute, fallbackAttributeKey: fallbackAttribute);

            var meta = experiment.Meta?.Count > 0 ? experiment.Meta[variationIndex] : null;

            var result = new ExperimentResult
            {
                Key = meta?.Key ?? variationIndex.ToString(),
                FeatureId = featureId,
                InExperiment = inExperiment,
                HashAttribute = hashAttribute,
                HashUsed = hashUsed,
                HashValue = hashValue,
                Value = experiment.Variations is null ? null : experiment.Variations[variationIndex],
                VariationId = variationIndex,
                Name = meta?.Name,
                Passthrough = meta?.Passthrough ?? false,
                Bucket = bucketHash ?? 0d,
                StickyBucketUsed = wasStickyBucketUsed
            };

            result.Name = meta?.Name;
            result.Passthrough = meta?.Passthrough ?? false;
            result.Bucket = bucketHash ?? 0d;

            return result;
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

            if (!_tracked.Contains(key))
            {
                try
                {
                    _trackingCallback(experiment, result);
                    _tracked.Add(key);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Encountered unhandled exception during tracking callback for experiment with combined key \'{Key}\'", key);
                }
            }
        }
    }
}
