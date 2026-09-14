using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Extensions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Api
{
    public class FeatureRepository : IGrowthBookFeatureRepository
    {
        private readonly ILogger<FeatureRepository> _logger;
        private readonly IGrowthBookFeatureCache _cache;
        private readonly IGrowthBookFeatureRefreshWorker _backgroundRefreshWorker;
        private readonly IRemoteEvaluationService _remoteEvaluationService;
        private readonly ConcurrentDictionary<string, ExperimentAssignment> _assigned;
        private readonly ConcurrentDictionary<string, byte> _tracked;

        private const int MaxBackoffMilliseconds = 5 * 60 * 1000;
        private const int BaseBackoffMilliseconds = 1000;

        private static readonly Random JitterSource = new Random();

        private readonly Func<DateTime> _utcNow;
        private readonly object _backoffLock = new object();
        private int _consecutiveFailures;
        private DateTime _nextAttemptAllowedAt = DateTime.MinValue;
        private Task<IDictionary<string, Feature>> _inFlightRefresh;
        private Task _lastRecordedRefresh;

        public FeatureRepository(ILogger<FeatureRepository> logger, IGrowthBookFeatureCache cache, IGrowthBookFeatureRefreshWorker backgroundRefreshWorker, IRemoteEvaluationService remoteEvaluationService = null)
            : this(logger, cache, backgroundRefreshWorker, remoteEvaluationService, () => DateTime.UtcNow)
        {
        }

        /// <summary>
        /// Creates a repository that reads the current time from <paramref name="utcNow"/> instead of the
        /// system clock, so the retry backoff can be tested without waiting it out.
        /// </summary>
        internal FeatureRepository(ILogger<FeatureRepository> logger, IGrowthBookFeatureCache cache, IGrowthBookFeatureRefreshWorker backgroundRefreshWorker, IRemoteEvaluationService remoteEvaluationService, Func<DateTime> utcNow)
        {
            _logger = logger;
            _cache = cache;
            _backgroundRefreshWorker = backgroundRefreshWorker;
            _assigned = new ConcurrentDictionary<string, ExperimentAssignment>();
            _tracked = new ConcurrentDictionary<string, byte>();
            _remoteEvaluationService = remoteEvaluationService;
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        /// <inheritdoc/>
        public void Cancel() => _backgroundRefreshWorker.Cancel();

        /// <inheritdoc/>
        public async Task<IDictionary<string, Feature>> GetFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            _logger.LogInformation("Getting features from repository, verifying cache expiration and option to force refresh");

            // We only want to try the actual API to retrieve features, as opposed to the cache,
            // when it's expired or we're overriding that to force a refresh. When the cache is
            // first initialized, it should be pre-expired so that this is automatically hit
            // in order to populate the initial cache values.

            var isCacheExpired = _cache.IsCacheExpired;

            var isForced = options?.ForceRefresh == true;

            if (isCacheExpired || isForced)
            {
                var featureCount = _cache.FeatureCount;
                var mustWait = featureCount == 0 || options?.WaitForCompletion == true;

                // Suppressed only for the automatic, expiry-driven refresh, and only while there is
                // something cached to serve instead. A caller who asked for a refresh outright is owed an
                // attempt: skipping it here would still report success, telling them a fetch happened when
                // none did. An empty cache has no stale value to fall back on either.
                if (!isForced && featureCount > 0 && !IsRefreshAllowedNow())
                {
                    _logger.LogDebug("Skipping the refresh: the previous attempt failed and the backoff window has not elapsed");

                    return await _cache.GetFeatures(cancellationToken);
                }

                _logger.LogInformation("Cache has expired or option to force refresh was set, refreshing the cache from the API");
                _logger.LogDebug("Cache expired: \'{CacheIsCacheExpired}\' and option to force refresh: \'{OptionsForceRefresh}\'", isCacheExpired, options?.ForceRefresh);

                // Use TaskFactory.StartNew to decouple from the current SynchronizationContext
                // This prevents threading issues in .NET Framework MVC when the original HttpContext
                // thread is no longer available after the HTTP request completes
                var refreshTask = StartOrJoinRefresh(cancellationToken);

                // When there aren't any features in the cache to begin with, we need to just wait until
                // that has been officially refreshed to proceed (otherwise the caller gets nothing up front
                // and has no way of determining when to check back). The other way to wait is if they explicitly
                // have noted that this is something they'd like to do.
                if (mustWait)
                {
                    _logger.LogInformation("Either cache currently has no features or the option to wait for completion was set, waiting for cache to refresh");
                    _logger.LogDebug("Feature count: '{CacheFeatureCount}' and option to wait for completion: '{OptionsWaitForCompletion}'", featureCount, options?.WaitForCompletion);

                    try
                    {
                        var features = await refreshTask;

                        // Recorded synchronously, before returning: a following sequential call must not
                        // reach the gate before this outcome is known, or it fires a second request.
                        RecordRefreshOutcome(refreshTask, features != null);

                        return features;
                    }
                    catch
                    {
                        RecordRefreshOutcome(refreshTask, false);
                        throw;
                    }
                }

                _ = refreshTask.ContinueWith(t =>
                {
                    // Not !IsFaulted: an escaping OperationCanceledException completes the task as
                    // Canceled, which is exactly what HttpClient raises when its Timeout elapses. Reading
                    // Result then throws inside the continuation, whose exception nothing observes.
                    RecordRefreshOutcome(t, t.Status == TaskStatus.RanToCompletion && t.Result != null);

                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "Background cache refresh failed");
                    }
                    else if (t.IsCanceled)
                    {
                        _logger.LogWarning("Background cache refresh was cancelled or timed out");
                    }
                });
            }

            _logger.LogInformation("Cache is not expired and the option to force refresh was not set, retrieving features from cache");

            return await _cache.GetFeatures(cancellationToken);
        }


        /// <summary>
        /// Returns the refresh already in flight, or starts one. Concurrent callers share a single request
        /// rather than each firing their own, which is also what lets the backoff work: without this, N
        /// callers arriving together all start a fetch before the first failure has been recorded.
        /// Mirrors the reference SDK's activeFetches map.
        /// </summary>
        private Task<IDictionary<string, Feature>> StartOrJoinRefresh(CancellationToken? cancellationToken)
        {
            lock (_backoffLock)
            {
                var inFlight = _inFlightRefresh;

                if (inFlight != null && !inFlight.IsCompleted)
                {
                    return inFlight;
                }

                var taskFactory = new TaskFactory(cancellationToken ?? CancellationToken.None);
                var started = taskFactory.StartNew(async () => await _backgroundRefreshWorker.RefreshCacheFromApi(cancellationToken)).Unwrap();

                _inFlightRefresh = started;

                return started;
            }
        }

        /// <summary>
        /// Whether a background refresh may start. After a failed attempt the next one is held off for an
        /// exponentially growing window, so an unreachable API is not hit once per evaluation.
        /// </summary>
        /// <remarks>
        /// There is deliberately no attempt limit: the window grows to <see cref="MaxBackoffMilliseconds"/>
        /// and stays there, matching the reference SDK, which backs its poller off without ever giving up.
        /// Giving up permanently would leave the caller on a stale cache with no way back.
        /// </remarks>
        private bool IsRefreshAllowedNow()
        {
            lock (_backoffLock)
            {
                return _consecutiveFailures == 0 || _utcNow() >= _nextAttemptAllowedAt;
            }
        }

        /// <summary>
        /// Records how one fetch ended, at most once for that fetch.
        /// </summary>
        /// <remarks>
        /// Every caller that joined an in-flight fetch reports its outcome, so without keying on the task
        /// itself a single failed request would advance the failure count once per joiner and overshoot
        /// the backoff window badly.
        /// </remarks>
        private void RecordRefreshOutcome(Task refresh, bool succeeded)
        {
            lock (_backoffLock)
            {
                if (ReferenceEquals(_lastRecordedRefresh, refresh))
                {
                    return;
                }

                _lastRecordedRefresh = refresh;

                if (succeeded)
                {
                    _consecutiveFailures = 0;
                    _nextAttemptAllowedAt = DateTime.MinValue;

                    return;
                }

                _consecutiveFailures++;

                double jitterFactor;

                lock (JitterSource)
                {
                    jitterFactor = 1d + JitterSource.NextDouble();
                }

                var delay = Math.Min(
                    BaseBackoffMilliseconds * Math.Pow(2, _consecutiveFailures - 1) * jitterFactor,
                    MaxBackoffMilliseconds);

                _nextAttemptAllowedAt = _utcNow().AddMilliseconds(delay);

                _logger.LogWarning("Feature fetch failed. Consecutive failure {Attempt}, next attempt allowed in {Delay}ms", _consecutiveFailures, (int)delay);
            }
        }

        /// <inheritdoc/>
        public bool HasIdenticalAssignment(string experimentKey, ExperimentAssignment assignment)
        {
            if (!_assigned.TryGetValue(experimentKey, out ExperimentAssignment prev))
            {
                return false;
            }

            return prev.Result.InExperiment == assignment.Result.InExperiment
                && prev.Result.VariationId == assignment.Result.VariationId;
        }

        /// <inheritdoc/>
        public void RecordAssignment(string experimentKey, ExperimentAssignment assignment)
        {
            _assigned.AddOrUpdate(experimentKey, assignment, (key, oldValue) => assignment);
        }

        /// <inheritdoc/>
        public bool IsAlreadyTracked(string trackingKey)
        {
            return _tracked.ContainsKey(trackingKey);
        }

        /// <inheritdoc/>
        public void MarkAsTracked(string trackingKey)
        {
            _tracked.TryAdd(trackingKey, 0);
        }

        /// <inheritdoc/>
        public bool TryMarkAsTracked(string trackingKey)
        {
            return _tracked.TryAdd(trackingKey, 0);
        }
 /// <inheritdoc/>
        public async Task<IDictionary<string, Feature>> GetFeaturesWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // If remote evaluation is not enabled, fall back to regular feature retrieval
            if (!context.RemoteEval)
            {
                _logger.LogDebug("Remote evaluation is disabled, using regular feature retrieval");
                return await GetFeatures(options, cancellationToken);
            }

            // Validate remote evaluation configuration
            if (_remoteEvaluationService == null)
            {
                _logger.LogWarning("Remote evaluation is enabled but IRemoteEvaluationService is not available, falling back to regular feature retrieval");
                return await GetFeatures(options, cancellationToken);
            }

            try
            {
                _remoteEvaluationService.ValidateRemoteEvaluationConfiguration(context);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Remote evaluation configuration is invalid: {Message}", ex.Message);
                throw;
            }

            _logger.LogInformation("Remote evaluation is enabled, performing remote feature evaluation");

            try
            {
                // Create remote evaluation request from context
                var request = RemoteEvaluationRequest.FromContext(context);

                // Perform remote evaluation
                var response = await _remoteEvaluationService.EvaluateAsync(
                    context.ApiHost,
                    context.ClientKey,
                    request,
                    GetApiRequestHeaders(context),
                    cancellationToken ?? CancellationToken.None);

                if (response.IsSuccess)
                {
                    _logger.LogInformation("Remote evaluation successful, received {Count} features", response.Features.Count);
                    return response.Features;
                }
                else
                {
                    var errorMessage = $"Remote evaluation failed: {response.ErrorMessage}";
                    _logger.LogError(errorMessage);
                    throw new Exceptions.RemoteEvaluationException(errorMessage, (int)response.StatusCode);
                }
            }
            catch (Exceptions.RemoteEvaluationException)
            {
                // Re-throw remote evaluation exceptions as-is
                throw;
            }
            catch (Exception ex)
            {
                var errorMessage = $"Unexpected error during remote evaluation: {ex.Message}";
                _logger.LogError(ex, errorMessage);
                throw new Exceptions.RemoteEvaluationException(errorMessage, null, ex);
            }
        }

        private IDictionary<string, string> GetApiRequestHeaders(Context context)
        {
            // For now, return empty headers. This can be extended later to support custom headers
            // from context or configuration
            return new Dictionary<string, string>();
        }
    }
}
