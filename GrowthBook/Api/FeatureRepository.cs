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

        public FeatureRepository(ILogger<FeatureRepository> logger, IGrowthBookFeatureCache cache, IGrowthBookFeatureRefreshWorker backgroundRefreshWorker)
        {
            _logger = logger;
            _cache = cache;
            _backgroundRefreshWorker = backgroundRefreshWorker;
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

                var refreshTask = _backgroundRefreshWorker.RefreshCacheFromApi(cancellationToken);

                // When there aren't any features in the cache to begin with, we need to just wait until
                // that has been officially refreshed to proceed (otherwise the caller gets nothing up front
                // and has no way of determining when to check back). The other way to wait is if they explicitly
                // have noted that this is something they'd like to do.

                if (_cache.FeatureCount == 0 || options?.WaitForCompletion == true)
                {
                    _logger.LogInformation("Either cache currently has no features or the option to wait for completion was set, waiting for cache to refresh");
                    _logger.LogDebug("Feature count: \'{CacheFeatureCount}\' and option to wait for completion: \'{OptionsWaitForCompletion}\'", _cache.FeatureCount, options?.WaitForCompletion);

                    return await refreshTask;
                }
            }

            _logger.LogInformation("Cache is not expired and the option to force refresh was not set, retrieving features from cache");

            return await _cache.GetFeatures(cancellationToken);
        }
    }
}
