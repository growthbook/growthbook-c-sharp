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
        private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _lruList;
        private readonly object _lock = new object();

        private class CacheItem
        {
            public string Url { get; set; }
            public string ETag { get; set; }
        }

        /// <summary>
        /// Initializes a new instance of the LruETagCache class.
        /// </summary>
        /// <param name="maxSize">Maximum number of entries to store (default: 100)</param>
        public LruETagCache(int maxSize = 100)
        {
            _maxSize = Math.Max(1, maxSize);
            _cache = new Dictionary<string, LinkedListNode<CacheItem>>(maxSize);
            _lruList = new LinkedList<CacheItem>();
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
                if (!_cache.TryGetValue(url, out var node))
                {
                    return null;
                }

                _lruList.Remove(node);
                _lruList.AddFirst(node);

                return node.Value.ETag;
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

                if (_cache.TryGetValue(url, out var existingNode))
                {
                    existingNode.Value.ETag = etag;
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                }
                else
                {
                    if (_cache.Count >= _maxSize)
                    {
                        var lruNode = _lruList.Last;
                        _cache.Remove(lruNode.Value.Url);
                        _lruList.RemoveLast();
                    }

                    var newItem = new CacheItem { Url = url, ETag = etag };
                    var newNode = _lruList.AddFirst(newItem);
                    _cache[url] = newNode;
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
                if (!_cache.TryGetValue(url, out var node))
                {
                    return null;
                }

                _cache.Remove(url);
                _lruList.Remove(node);

                return node.Value.ETag;
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
                _lruList.Clear();
            }
        }
    }
}
