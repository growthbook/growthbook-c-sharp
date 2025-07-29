using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
using Xunit;

namespace GrowthBook.Tests.Api
{
    /// <summary>
    /// Tests for file-based feature cache - similar to Swift CachingManager tests.
    /// </summary>
    public class InMemoryFeatureCacheTests : IDisposable
    {
        private readonly InMemoryFeatureCache _cache;

        public InMemoryFeatureCacheTests()
        {
            _cache = new InMemoryFeatureCache();
        }

        [Fact]
        public async Task TestCaching()
        {
            // Arrange
            var features = new Dictionary<string, Feature>
            {
                ["GrowthBook"] = new Feature { DefaultValue = "GrowthBook" }
            };

            // Act
            await _cache.RefreshWith(features);
            var retrievedFeatures = await _cache.GetFeatures();

            // Assert
            retrievedFeatures.Should().ContainKey("GrowthBook");
            retrievedFeatures["GrowthBook"].DefaultValue.ToString().Should().Be("GrowthBook");
        }

        [Fact]
        public async Task TestClearCache()
        {
            // Arrange
            var features = new Dictionary<string, Feature>
            {
                ["GrowthBook"] = new Feature { DefaultValue = "GrowthBook" }
            };

            // Act
            await _cache.RefreshWith(features);
            _cache.ClearCache();
            var retrievedFeatures = await _cache.GetFeatures();

            // Assert
            retrievedFeatures.Should().BeEmpty();
        }

        [Fact]
        public void TestSetCacheKey()
        {
            // Act & Assert
            _cache.Invoking(c => c.SetCacheKey("test-key")).Should().NotThrow();
        }

        public void Dispose()
        {
            _cache?.ClearCache();
        }
    }
}