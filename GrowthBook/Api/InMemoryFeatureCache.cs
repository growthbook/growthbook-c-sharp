using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook.Api
{
    /// <summary>
    /// Represents a simple in-memory cache for GrowthBook features.
    /// </summary>
    public class InMemoryFeatureCache : IGrowthBookFeatureCache
    {
        private readonly object _cacheLock = new object();
        private IDictionary<string, Feature> _cachedFeatures = new Dictionary<string, Feature>();
        private readonly int _cacheExpirationInSeconds;
        private DateTime _nextCacheExpiration;

        public InMemoryFeatureCache(int cacheExpirationInSeconds)
        {
            _cacheExpirationInSeconds = cacheExpirationInSeconds;
            _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);
        }

        public int FeatureCount
        {
            get
            {
                lock(_cacheLock)
                {
                    return _cachedFeatures.Count;
                }
            }
        }

        public bool IsCacheExpired
        {
            get
            {
                lock(_cacheLock)
                {
                    return _nextCacheExpiration <= DateTime.UtcNow;
                }
            }
        }

        public Task<IDictionary<string, Feature>> GetFeatures(CancellationToken? cancellationToken = null)
        {
            lock (_cacheLock)
            {
                return Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature>(_cachedFeatures));
            }
        }

        public async Task RefreshWith(IDictionary<string, Feature> features, CancellationToken? cancellationToken = null)
        {
            lock(_cacheLock)
            {
                _cachedFeatures = new Dictionary<string, Feature>(features);
                _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);
            }
        }
    }
}
