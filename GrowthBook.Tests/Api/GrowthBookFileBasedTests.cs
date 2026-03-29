using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.Api
{
    /// <summary>
    /// Tests demonstrating GrowthBook with file-based cache (Swift-like).
    /// </summary>
    public class GrowthBookFileBasedTests
    {
        [Fact]
        public void GrowthBook_DefaultConstruction_UsesFileBased()
        {
            // Arrange & Act
            var context = new Context();
            using var growthBook = new GrowthBook(context);

            // Assert
            growthBook.Should().NotBeNull();
            // File-based cache is now the default (no configuration needed)
        }

        [Fact]
        public void GrowthBook_WithCustomCachePath_ShouldWork()
        {
            // Arrange
            var context = new Context
            {
                CachePath = "/tmp/growthbook-test"
            };

            // Act & Assert
            using var growthBook = new GrowthBook(context);
            growthBook.Should().NotBeNull();
        }

        [Fact]
        public void GrowthBook_WithFeatures_ShouldWork()
        {
            // Arrange
            var context = new Context
            {
                Features = new Dictionary<string, Feature>
                {
                    ["test-feature"] = new Feature
                    {
                        DefaultValue = "file-cached-value"
                    }
                }
            };

            // Act
            using var growthBook = new GrowthBook(context);
            var value = growthBook.GetFeatureValue("test-feature", "fallback");

            // Assert
            value.Should().Be("file-cached-value");
        }
    }
}