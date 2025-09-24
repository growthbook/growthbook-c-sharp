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

        public FeatureRepository(ILogger<FeatureRepository> logger, IGrowthBookFeatureCache cache, IGrowthBookFeatureRefreshWorker backgroundRefreshWorker, IRemoteEvaluationService remoteEvaluationService = null)
        {
            _logger = logger;
            _cache = cache;
            _backgroundRefreshWorker = backgroundRefreshWorker;
            _assigned = new ConcurrentDictionary<string, ExperimentAssignment>();
            _tracked = new ConcurrentDictionary<string, byte>();
            _remoteEvaluationService = remoteEvaluationService;
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

            if (_cache.IsCacheExpired || options?.ForceRefresh == true)
            {
                _logger.LogInformation("Cache has expired or option to force refresh was set, refreshing the cache from the API");
                _logger.LogDebug("Cache expired: \'{CacheIsCacheExpired}\' and option to force refresh: \'{OptionsForceRefresh}\'", _cache.IsCacheExpired, options?.ForceRefresh);

                // Use TaskFactory.StartNew to decouple from the current SynchronizationContext
                // This prevents threading issues in .NET Framework MVC when the original HttpContext
                // thread is no longer available after the HTTP request completes
                var taskFactory = new TaskFactory(cancellationToken ?? CancellationToken.None);
                var refreshTask = taskFactory.StartNew(async () => await _backgroundRefreshWorker.RefreshCacheFromApi(cancellationToken)).Unwrap();

                // When there aren't any features in the cache to begin with, we need to just wait until
                // that has been officially refreshed to proceed (otherwise the caller gets nothing up front
                // and has no way of determining when to check back). The other way to wait is if they explicitly
                // have noted that this is something they'd like to do.
                if (_cache.FeatureCount == 0 || options?.WaitForCompletion == true)
                {
                    _logger.LogInformation("Either cache currently has no features or the option to wait for completion was set, waiting for cache to refresh");
                    _logger.LogDebug("Feature count: '{CacheFeatureCount}' and option to wait for completion: '{OptionsWaitForCompletion}'", _cache.FeatureCount, options?.WaitForCompletion);
                    return await refreshTask;
                }
                else
                {
                    // Start the refresh but don't wait - fire and forget
                    _ = refreshTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _logger.LogError(t.Exception, "Background cache refresh failed");
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            _logger.LogInformation("Cache is not expired and the option to force refresh was not set, retrieving features from cache");

            return await _cache.GetFeatures(cancellationToken);
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
