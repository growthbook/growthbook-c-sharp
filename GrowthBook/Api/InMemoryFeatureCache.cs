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
        private readonly Func<DateTime> _utcNow;
        private DateTime _nextCacheExpiration;

        public InMemoryFeatureCache(int cacheExpirationInSeconds)
            : this(cacheExpirationInSeconds, () => DateTime.UtcNow)
        {
        }

        /// <summary>
        /// Creates a cache that reads the current time from <paramref name="utcNow"/> instead of the system
        /// clock, so that expiry can be tested without waiting out the TTL in real seconds.
        /// </summary>
        /// <param name="cacheExpirationInSeconds">How long a refreshed cache stays fresh.</param>
        /// <param name="utcNow">Source of the current UTC time.</param>
        internal InMemoryFeatureCache(int cacheExpirationInSeconds, Func<DateTime> utcNow)
        {
            // The cache should start out in an expired state so that any exterior logic
            // based off of that can feel free to retrieve things to cache as soon as it needs to.

            _cacheExpirationInSeconds = cacheExpirationInSeconds;
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _nextCacheExpiration = _utcNow();
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
                    return _nextCacheExpiration <= _utcNow();
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
                _nextCacheExpiration = _utcNow().AddSeconds(_cacheExpirationInSeconds);

                return Task.CompletedTask;
            }
        }

        internal Task RefreshExpiration(CancellationToken? cancellationToken = null)
        {
            lock(_cacheLock)
            {
                _nextCacheExpiration = _utcNow().AddSeconds(_cacheExpirationInSeconds);
                return Task.CompletedTask;
            }
        }
    }
}
