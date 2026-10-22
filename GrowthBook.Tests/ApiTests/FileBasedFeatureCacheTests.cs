using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

public class FileBasedFeatureCacheTests : IDisposable
{
    private const string FirstFeatureId = nameof(FirstFeatureId);
    private const string SecondFeatureId = nameof(SecondFeatureId);

    private readonly string _testCachePath;
    private readonly FileBasedFeatureCache _cache;
    private readonly Feature _firstFeature;
    private readonly Feature _secondFeature;
    private readonly Dictionary<string, Feature> _availableFeatures;

    public FileBasedFeatureCacheTests()
    {
        _testCachePath = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}");

        _cache = new FileBasedFeatureCache("client-under-test", cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        _firstFeature = new() { DefaultValue = 1 };
        _secondFeature = new() { DefaultValue = 2 };
        _availableFeatures = new()
        {
            [FirstFeatureId] = _firstFeature,
            [SecondFeatureId] = _secondFeature
        };
    }

    [Fact]
    public void CacheIsImmediatelyExpiredUponCreation()
    {
        _cache.IsCacheExpired.Should().BeTrue("because no attempt to refresh the cache with features has been made");
    }

    [Fact]
    public async Task FeatureCountAccuratelyReflectsCachedFeatures()
    {
        _cache.FeatureCount.Should().Be(0, "because no features have been cached yet");

        await _cache.RefreshWith(_availableFeatures);

        _cache.FeatureCount.Should().Be(_availableFeatures.Count, "because that's the number of features that were cached");
    }

    [Fact]
    public async Task CacheExpirationStatusWillChangeWhenCacheIsRefreshed()
    {
        _cache.IsCacheExpired.Should().BeTrue("because no attempt to refresh the cache with features has been made");

        await _cache.RefreshWith(_availableFeatures);

        _cache.IsCacheExpired.Should().BeFalse("because the expiration date shifts forward when the cache is refreshed");
    }

    [Fact]
    public async Task GetFeaturesWillRetrieveCopyOfCache()
    {
        await _cache.RefreshWith(_availableFeatures);

        var features = await _cache.GetFeatures();

        features.Should().NotBeNullOrEmpty("because at least one feature has been cached");
        features.Should().NotBeSameAs(_availableFeatures, "because a copy of the cache will be returned to discourage external cache manipulation");
        features.Should().BeEquivalentTo(_availableFeatures, "because all cached features will be present");
    }

    [Fact]
    public async Task FeaturesArePersistedToDiskAfterRefresh()
    {
        await _cache.RefreshWith(_availableFeatures);

        var secondCache = new FileBasedFeatureCache("client-under-test", cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        var features = await secondCache.GetFeatures();

        features.Should().NotBeEmpty("because features were persisted to disk by the first cache instance");
        features.Should().ContainKey(FirstFeatureId);
        features.Should().ContainKey(SecondFeatureId);
    }

    [Fact]
    public async Task ClearCacheRemovesBothMemoryAndFileData()
    {
        await _cache.RefreshWith(_availableFeatures);

        _cache.ClearCache();

        _cache.FeatureCount.Should().Be(0, "because clearing the cache empties the in-memory features");
        _cache.IsCacheExpired.Should().BeTrue("because clearing the cache resets the expiration");

        var secondCache = new FileBasedFeatureCache("client-under-test", cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        var features = await secondCache.GetFeatures();
        features.Should().BeEmpty("because clearing the cache deletes the file from disk");
    }

    [Fact]
    public async Task SetCacheKeyIsolatesCacheByDirectory()
    {
        _cache.SetCacheKey("key-a");
        await _cache.RefreshWith(_availableFeatures);

        var otherCache = new FileBasedFeatureCache("key-b", cacheExpirationInSeconds: 60, cachePath: _testCachePath);

        var features = await otherCache.GetFeatures();
        features.Should().BeEmpty("because a different cache key maps to a different directory");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCachePath))
            {
                Directory.Delete(_testCachePath, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void ACacheKeyIsRequired()
    {
        // Without one every instance sharing a path reads and writes the same file, so one client's
        // features are served to another until its own refresh lands.
        var build = () => new FileBasedFeatureCache(null, cachePath: _testCachePath);

        build.Should().Throw<ArgumentException>().WithMessage("*client key*");

        var buildBlank = () => new FileBasedFeatureCache("   ", cachePath: _testCachePath);

        buildBlank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task TwoClientsSharingAPathDoNotSeeEachOthersFeatures()
    {
        var first = new FileBasedFeatureCache("client-a", cachePath: _testCachePath);

        await first.RefreshWith(_availableFeatures);

        var second = new FileBasedFeatureCache("client-b", cachePath: _testCachePath);

        (await second.GetFeatures()).Should().BeEmpty(
            "a cache belongs to the client it was keyed for, not to whoever shares the directory");
    }

    [Fact]
    public async Task ChangingTheCacheKeyDropsTheFeaturesOfThePreviousOne()
    {
        _cache.SetCacheKey("client-a");
        await _cache.RefreshWith(_availableFeatures);

        _cache.SetCacheKey("client-b");

        (await _cache.GetFeatures()).Should().BeEmpty(
            "the features held belonged to the previous key, so they cannot answer for this one");
        _cache.FeatureCount.Should().Be(0);
        _cache.IsCacheExpired.Should().BeTrue(
            "and nothing has been cached for this key yet, so a load has to happen");
    }

    [Fact]
    public async Task ChangingTheCacheKeyBackFindsWhatWasStoredForIt()
    {
        _cache.SetCacheKey("client-a");
        await _cache.RefreshWith(_availableFeatures);

        _cache.SetCacheKey("client-b");
        _cache.SetCacheKey("client-a");

        (await _cache.GetFeatures()).Should().ContainKey(FirstFeatureId,
            "coming back to a key reads what was written for it rather than starting empty");
    }

    [Fact]
    public async Task ChangingTheCachePathDropsTheFeaturesFromTheOldOne()
    {
        await _cache.RefreshWith(_availableFeatures);

        var otherPath = Path.Combine(Path.GetTempPath(), $"gb-test-{Guid.NewGuid():N}");

        try
        {
            _cache.SetCustomCachePath(otherPath);

            (await _cache.GetFeatures()).Should().BeEmpty(
                "the features came from the previous directory and say nothing about this one");
        }
        finally
        {
            if (Directory.Exists(otherPath))
            {
                Directory.Delete(otherPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AHalfWrittenFileIsNeverLeftBehind()
    {
        await _cache.RefreshWith(_availableFeatures);

        var directory = Directory.GetDirectories(_testCachePath).Single();
        var written = Directory.GetFiles(directory);

        written.Should().ContainSingle("the temporary file is moved into place, not left beside the real one");
        Path.GetFileName(written[0]).Should().Be("gb-features.json");

        // Whatever is on disk has to be a complete document, not a prefix of one.
        var reread = new FileBasedFeatureCache("client-under-test", cachePath: _testCachePath);

        (await reread.GetFeatures()).Should().HaveCount(_availableFeatures.Count);
    }

    [Fact]
    public async Task TheDirectoryNameKeepsTheWholeHash()
    {
        await _cache.RefreshWith(_availableFeatures);

        // Eight hex characters is 32 bits, which is small enough for two client keys to collide and
        // read each other's features - and separating them is the only thing the hash is for.
        var directory = Directory.GetDirectories(_testCachePath).Single();
        var hash = Path.GetFileName(directory).Replace("GrowthBook-Cache-", string.Empty);

        hash.Should().HaveLength(64, "a SHA256 digest in hex, not a prefix of one");
    }

    /// <summary>
    /// The directory name is derived from the client key, so it is predictable. On a shared temporary
    /// directory another local account could create it first and leave a file for this process to load
    /// and evaluate, so the default lives in the user's own tree instead.
    /// </summary>
    [Fact]
    public void TheDefaultBasePathIsInsideTheUsersOwnApplicationData()
    {
        var resolved = FileBasedFeatureCache.ResolveDefaultBasePath("/home/someone/.local/share", logger: null);

        resolved.Should().Be(Path.Combine("/home/someone/.local/share", "GrowthBook"));
        resolved.Should().NotStartWith(Path.GetTempPath(),
            "the temporary directory is shared on most Unix hosts, and another account could create " +
            "the predictable directory first and leave a payload for this process to evaluate");
    }

    /// <summary>
    /// Some hosts report no application data directory at all. The cache still has to work, but the
    /// directory it falls back to may be shared, so the fallback is not silent.
    /// </summary>
    [Fact]
    public void WithNoApplicationDataTheBasePathFallsBackToTemporaryAndSaysSo()
    {
        var logger = new CapturingLogger();

        var resolved = FileBasedFeatureCache.ResolveDefaultBasePath(string.Empty, logger);

        resolved.Should().Be(Path.GetTempPath());
        logger.Messages.Should().Contain(m => m.Contains("falls back to the temporary directory"),
            "a caller whose cache landed somewhere shared has to be able to find that out");
    }

    /// <summary>
    /// And the cache really uses that decision rather than defaulting somewhere of its own. Checked
    /// against whatever this host reports, so the suite does not depend on the host having, or not
    /// having, an application data directory.
    /// </summary>
    [Fact]
    public async Task TheCacheWritesUnderTheResolvedDefaultBasePath()
    {
        // A key no previous run can have used, so a leftover directory cannot answer for the fix.
        var key = $"default-path-{Guid.NewGuid():N}";
        var directoryName = $"GrowthBook-Cache-{Sha256Hex(key)}";

        var expectedBase = FileBasedFeatureCache.ResolveDefaultBasePath(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), logger: null);

        var expected = Path.Combine(expectedBase, directoryName);

        var cache = new FileBasedFeatureCache(key);

        try
        {
            await cache.RefreshWith(_availableFeatures);

            Directory.Exists(expected).Should().BeTrue(
                "the cache writes under the base path the default resolution chose");
        }
        finally
        {
            cache.ClearCache();
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<FileBasedFeatureCache>
    {
        public List<string> Messages { get; } = new List<string>();

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private static string Sha256Hex(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();

        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));

        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }
}
