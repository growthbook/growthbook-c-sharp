using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GrowthBook.Api
{
    /// <summary>
    /// LRU (Least Recently Used) cache for storing ETags.
    /// 
    /// This cache has a maximum capacity and automatically evicts the least recently
    /// accessed entries when the capacity is exceeded.
    /// </summary>
    public class LruETagCache
    {
        private readonly int _maxSize;
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private readonly Dictionary<string, long> _accessOrder = new Dictionary<string, long>();
        private long _accessCounter = 0;
        private readonly object _lock = new object();

        /// <summary>
        /// Initializes a new instance of the LruETagCache class.
        /// </summary>
        /// <param name="maxSize">Maximum number of entries to store (default: 100)</param>
        public LruETagCache(int maxSize = 100)
        {
            _maxSize = Math.Max(1, maxSize);
        }

        /// <summary>
        /// Get the ETag for a URL, updating its access order.
        /// </summary>
        /// <param name="url">The URL to look up</param>
        /// <returns>The ETag value, or null if not found</returns>
        public string Get(string url)
        {
            if (url == null)
            {
                return null;
            }

            lock (_lock)
            {
                if (!_cache.ContainsKey(url))
                {
                    return null;
                }

                // Update access order (move to most recently used)
                _accessOrder[url] = ++_accessCounter;

                return _cache[url];
            }
        }

        /// <summary>
        /// Store an ETag for a URL.
        /// 
        /// If the ETag is null, the entry will be removed.
        /// If capacity is exceeded, the least recently used entry will be evicted.
        /// </summary>
        /// <param name="url">The URL to store</param>
        /// <param name="etag">The ETag value, or null to remove</param>
        public void Put(string url, string etag)
        {
            if (url == null)
            {
                return;
            }

            lock (_lock)
            {
                if (etag == null)
                {
                    Remove(url);
                    return;
                }

                // Check if this is an update (not a new entry)
                bool isUpdate = _cache.ContainsKey(url);

                // Update or add the entry
                _cache[url] = etag;
                _accessOrder[url] = ++_accessCounter;

                // If not an update and we're over capacity, evict the LRU entry
                if (!isUpdate && _cache.Count > _maxSize)
                {
                    EvictLru();
                }
            }
        }

        /// <summary>
        /// Remove an entry from the cache.
        /// </summary>
        /// <param name="url">The URL to remove</param>
        /// <returns>The removed ETag value, or null if not found</returns>
        public string Remove(string url)
        {
            if (url == null)
            {
                return null;
            }

            lock (_lock)
            {
                if (!_cache.ContainsKey(url))
                {
                    return null;
                }

                string value = _cache[url];
                _cache.Remove(url);
                _accessOrder.Remove(url);

                return value;
            }
        }

        /// <summary>
        /// Check if a URL exists in the cache.
        /// </summary>
        /// <param name="url">The URL to check</param>
        /// <returns>True if the URL exists in the cache</returns>
        public bool Contains(string url)
        {
            if (url == null)
            {
                return false;
            }

            lock (_lock)
            {
                return _cache.ContainsKey(url);
            }
        }

        /// <summary>
        /// Get the current number of entries in the cache.
        /// </summary>
        /// <returns>The number of entries</returns>
        public int Size()
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }

        /// <summary>
        /// Clear all entries from the cache.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _accessOrder.Clear();
                _accessCounter = 0;
            }
        }

        /// <summary>
        /// Evict the least recently used entry from the cache.
        /// </summary>
        private void EvictLru()
        {
            if (_accessOrder.Count == 0)
            {
                return;
            }

            // Find the URL with the lowest access counter (LRU)
            var lruEntry = _accessOrder.OrderBy(kvp => kvp.Value).First();
            string lruUrl = lruEntry.Key;

            _cache.Remove(lruUrl);
            _accessOrder.Remove(lruUrl);
        }
    }
}
