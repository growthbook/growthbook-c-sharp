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

        /// <summary>
        /// Creates a cache backed by a file on disk.
        /// </summary>
        /// <param name="cacheKey">
        /// Identifies whose features these are, normally the client key. Required: without it every
        /// instance sharing a path would read and write one file, and one client's features would be
        /// served to another until its own refresh landed.
        /// </param>
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

        /// <summary>
        /// Points the cache at a different client's features.
        /// </summary>
        /// <remarks>
        /// The features held in memory belong to the previous key, so they are dropped and the new
        /// location is read instead. Keeping them would have <see cref="GetFeatures"/> answer with the
        /// previous client's data, and <see cref="IsCacheExpired"/> call it current - so nothing would
        /// have refreshed it either.
        /// </remarks>
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
            var basePath = _customCachePath ?? GetDefaultBasePath();

            return Path.Combine(basePath, $"GrowthBook-Cache-{_cacheKey}");
        }

        /// <summary>
        /// Where the cache lives when the caller names no directory.
        /// </summary>
        /// <remarks>
        /// The per-user application data directory, not the temporary one. A temporary directory is
        /// shared on most Unix hosts, and the directory name here is derived from the client key, so
        /// another local account could create it first and leave a <c>gb-features.json</c> of its own
        /// for this process to load and evaluate. Application data is inside the user's own tree and
        /// another account cannot write there.
        /// <para>
        /// A caller who passes <c>cachePath</c> owns that decision: this library targets netstandard2.0,
        /// which exposes neither file ownership nor permissions, so a supplied directory cannot be
        /// checked for either.
        /// </para>
        /// </remarks>
        private string GetDefaultBasePath()
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(applicationData))
            {
                return Path.Combine(applicationData, "GrowthBook");
            }

            // Some hosts report no application data directory at all. Falling back keeps the cache
            // working, but on a shared temporary directory it is worth saying so out loud.
            _logger?.LogWarning("No per-user application data directory is available, so the feature cache falls back to the temporary directory. Pass a cachePath this process controls if that directory is shared.");

            return Path.GetTempPath();
        }

        /// <summary>
        /// Hashes a cache key into a directory name.
        /// </summary>
        /// <remarks>
        /// The whole digest, not a prefix of it. Truncating to eight hex characters left 32 bits, which
        /// is small enough for two client keys to land in the same directory and read each other's
        /// features - and keeping them apart is the only thing the hash is for.
        /// </remarks>
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
