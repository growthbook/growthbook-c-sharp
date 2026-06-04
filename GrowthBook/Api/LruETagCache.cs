using System;
using System.Collections.Generic;

namespace GrowthBook.Api
{
    internal class LruETagCache
    {
        private readonly int _maxSize;
        private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _lruList = new LinkedList<CacheItem>();
        private readonly object _lock = new object();

        private class CacheItem
        {
            public string Url { get; set; }
            public string ETag { get; set; }
        }

        public LruETagCache(int maxSize = 100)
        {
            _maxSize = Math.Max(1, maxSize);
            _cache = new Dictionary<string, LinkedListNode<CacheItem>>(_maxSize);
        }

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
                    RemoveCore(url);
                    return;
                }

                if (_cache.TryGetValue(url, out var existingNode))
                {
                    existingNode.Value.ETag = etag;
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                    return;
                }

                if (_cache.Count >= _maxSize)
                {
                    var lruNode = _lruList.Last;
                    _cache.Remove(lruNode.Value.Url);
                    _lruList.RemoveLast();
                }

                var item = new CacheItem { Url = url, ETag = etag };
                _cache[url] = _lruList.AddFirst(item);
            }
        }

        public string Remove(string url)
        {
            if (url == null)
            {
                return null;
            }

            lock (_lock)
            {
                return RemoveCore(url);
            }
        }

        public int Size()
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }

        private string RemoveCore(string url)
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
}
