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
        // We're providing a lock and locking around every operation within this cache
        // because this is an in-memory cache and may be accessed by multiple threads
        // in an async manner, so we'd like to be safe and avoid some issues there.

        // As a side note, we're specifically doing this with a regular Dictionary and not using a
        // ConcurrentDictionary because we have other non-dictionary uses that need to be safely used
        // and would like to avoid confusion by mixing paradigms unnecessarily.

        private readonly object _cacheLock = new object();
        private IDictionary<string, Feature> _cachedFeatures = new Dictionary<string, Feature>();
        private readonly int _cacheExpirationInSeconds;
        private DateTime _nextCacheExpiration;

        public InMemoryFeatureCache(int cacheExpirationInSeconds)
        {
            // The cache should start out in an expired state so that any exterior logic
            // based off of that can feel free to retrieve things to cache as soon as it needs to.

            _cacheExpirationInSeconds = cacheExpirationInSeconds;
            _nextCacheExpiration = DateTime.UtcNow;
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

        public Task RefreshWith(IDictionary<string, Feature> features, CancellationToken? cancellationToken = null)
        {
            lock(_cacheLock)
            {
                _cachedFeatures = new Dictionary<string, Feature>(features);
                _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);

                return Task.CompletedTask;
            }
        }

        public Task RefreshExpiration(CancellationToken? cancellationToken = null)
        {
            lock(_cacheLock)
            {
                _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);
                return Task.CompletedTask;
            }
        }
    }
}
