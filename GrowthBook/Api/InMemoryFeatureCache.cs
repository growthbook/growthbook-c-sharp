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
    /// <summary>
    /// File-based feature cache similar to Swift CachingManager implementation.
    /// Provides persistent storage with SHA256 key hashing.
    /// </summary>
    public class InMemoryFeatureCache : IGrowthBookFeatureCache
    {
        private readonly object _cacheLock = new object();
        private IDictionary<string, Feature> _cachedFeatures = new Dictionary<string, Feature>();
        private readonly ILogger<InMemoryFeatureCache> _logger;
        private string _cacheKey = "";
        private string _customCachePath;
        private const string DefaultFileName = "gb-features";

        public InMemoryFeatureCache(int cacheExpirationInSeconds = 60, ILogger<InMemoryFeatureCache> logger = null)
        {
            _logger = logger;
            LoadFromFile();
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

        public bool IsCacheExpired => false; // File-based cache doesn't expire automatically

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
                SaveToFile();
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Set cache key (similar to Swift setCacheKey).
        /// </summary>
        public void SetCacheKey(string key)
        {
            lock(_cacheLock)
            {
                _cacheKey = CreateSha256Hash(key ?? "");
            }
        }

        /// <summary>
        /// Set custom cache directory path (similar to Swift setCustomCachePath).
        /// </summary>
        public void SetCustomCachePath(string path)
        {
            lock(_cacheLock)
            {
                _customCachePath = path;
            }
        }

        /// <summary>
        /// Clear all cached data (similar to Swift clearCache).
        /// </summary>
        public void ClearCache()
        {
            lock(_cacheLock)
            {
                try
                {
                    var directoryPath = GetCacheDirectoryPath();
                    if (Directory.Exists(directoryPath))
                    {
                        Directory.Delete(directoryPath, recursive: true);
                        _logger?.LogInformation("Cache cleared successfully: {DirectoryPath}", directoryPath);
                    }
                    _cachedFeatures.Clear();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to clear cache");
                }
            }
        }

        private void LoadFromFile()
        {
            lock(_cacheLock)
            {
                try
                {
                    var filePath = GetTargetFilePath();
                    if (File.Exists(filePath))
                    {
                        var content = File.ReadAllText(filePath);
                        var features = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(content);
                        _cachedFeatures = features ?? new Dictionary<string, Feature>();
                        _logger?.LogDebug("Loaded {Count} features from cache file", _cachedFeatures.Count);
                    }
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
                var filePath = GetTargetFilePath();
                var directory = Path.GetDirectoryName(filePath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Remove existing file if present (similar to Swift)
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                var content = JsonConvert.SerializeObject(_cachedFeatures, Formatting.None);
                File.WriteAllText(filePath, content);
                
                _logger?.LogDebug("Saved {Count} features to cache file", _cachedFeatures.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save features to cache file");
            }
        }

        private string GetTargetFilePath()
        {
            var cacheDirectory = GetCacheDirectoryPath();
            return Path.Combine(cacheDirectory, $"{DefaultFileName}.txt");
        }

        private string GetCacheDirectoryPath()
        {
            var basePath = _customCachePath ?? Path.GetTempPath();
            return Path.Combine(basePath, $"GrowthBook-Cache-{_cacheKey}");
        }

        private string CreateSha256Hash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";

            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                var hash = sha256.ComputeHash(bytes);
                var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return hashString.Substring(0, Math.Min(5, hashString.Length));
            }
        }
    }
}
