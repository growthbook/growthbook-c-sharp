using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace GrowthBook.Api
{
    public class FileBasedFeatureCache : IGrowthBookFeatureCache, IFeatureCacheExpiration
    {
        private readonly object _cacheLock = new object();
        private IDictionary<string, Feature> _cachedFeatures = new Dictionary<string, Feature>();
        private readonly ILogger<FileBasedFeatureCache> _logger;
        private readonly int _cacheExpirationInSeconds;
        private DateTime _nextCacheExpiration;
        private string _cacheKey = "";
        private string _customCachePath;
        private const string DefaultFileName = "gb-features";

        public FileBasedFeatureCache(string cacheKey, int cacheExpirationInSeconds = 60, string cachePath = null, ILogger<FileBasedFeatureCache> logger = null)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                throw new ArgumentException("A cache key is required so that one client's cached features cannot be served to another. Pass the client key.", nameof(cacheKey));
            }

            _cacheExpirationInSeconds = cacheExpirationInSeconds;
            _nextCacheExpiration = DateTime.UtcNow;
            _customCachePath = cachePath;
            _cacheKey = ComputeCacheKeyHash(cacheKey);
            _logger = logger;
            LoadFromFile();
        }

        public int FeatureCount
        {
            get
            {
                lock (_cacheLock)
                {
                    return _cachedFeatures.Count;
                }
            }
        }

        public bool IsCacheExpired
        {
            get
            {
                lock (_cacheLock)
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
            lock (_cacheLock)
            {
                _cachedFeatures = new Dictionary<string, Feature>(features);
                _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);
                SaveToFile();
                return Task.CompletedTask;
            }
        }

        /// <inheritdoc/>
        public Task RefreshExpiration(CancellationToken? cancellationToken = null)
        {
            lock (_cacheLock)
            {
                _nextCacheExpiration = DateTime.UtcNow.AddSeconds(_cacheExpirationInSeconds);
                return Task.CompletedTask;
            }
        }

        public void SetCacheKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A cache key is required so that one client's cached features cannot be served to another.", nameof(key));
            }

            lock (_cacheLock)
            {
                var hashed = ComputeCacheKeyHash(key);

                if (hashed == _cacheKey)
                {
                    return;
                }

                _cacheKey = hashed;

                ReloadForCurrentLocation();
            }
        }

        /// <summary>
        /// Points the cache at a different directory, dropping what the previous one held.
        /// </summary>
        public void SetCustomCachePath(string path)
        {
            lock (_cacheLock)
            {
                if (path == _customCachePath)
                {
                    return;
                }

                _customCachePath = path;

                ReloadForCurrentLocation();
            }
        }

        /// <summary>
        /// Drops what is held and reads whatever the current location has, if anything.
        /// </summary>
        private void ReloadForCurrentLocation()
        {
            _cachedFeatures = new Dictionary<string, Feature>();
            _nextCacheExpiration = DateTime.UtcNow;

            LoadFromFile();
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                try
                {
                    _cachedFeatures.Clear();
                    _nextCacheExpiration = DateTime.UtcNow;

                    var directory = GetCacheDirectoryPath();
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                        _logger?.LogInformation("Cache cleared: {Directory}", directory);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to clear cache");
                    _cachedFeatures.Clear();
                }
            }
        }

        private void LoadFromFile()
        {
            lock (_cacheLock)
            {
                try
                {
                    var filePath = GetFilePath();
                    if (!File.Exists(filePath))
                    {
                        return;
                    }

                    var content = File.ReadAllText(filePath);
                    var features = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(content);
                    _cachedFeatures = features ?? new Dictionary<string, Feature>();
                    _logger?.LogDebug("Loaded {Count} features from cache file", _cachedFeatures.Count);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load features from cache file");
                    _cachedFeatures = new Dictionary<string, Feature>();
                }
            }
        }

        private void SaveToFile()
        {
            try
            {
                var filePath = GetFilePath();
                var directory = Path.GetDirectoryName(filePath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var content = JsonConvert.SerializeObject(_cachedFeatures, Formatting.None);

                var temporaryPath = filePath + ".tmp";

                File.WriteAllText(temporaryPath, content);

                if (File.Exists(filePath))
                {
                    File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, filePath);
                }

                _logger?.LogDebug("Saved {Count} features to cache file", _cachedFeatures.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save features to cache file");
            }
        }

        private string GetFilePath()
        {
            return Path.Combine(GetCacheDirectoryPath(), $"{DefaultFileName}.json");
        }

        private string GetCacheDirectoryPath()
        {
            var basePath = _customCachePath ?? Path.GetTempPath();
            return Path.Combine(basePath, $"GrowthBook-Cache-{_cacheKey}");
        }

        
        private static string ComputeCacheKeyHash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));

                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
