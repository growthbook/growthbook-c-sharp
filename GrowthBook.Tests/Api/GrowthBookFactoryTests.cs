using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.Api
{
    /// <summary>
    /// Basic tests for GrowthBookFactory functionality.
    /// </summary>
    public class GrowthBookFactoryTests : IDisposable
    {
        [Fact]
        public void GrowthBookFactory_Constructor_WithNullContext_ShouldThrow()
        {
            // Act & Assert
            Action act = () => new GrowthBookFactory(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void GrowthBookFactory_CreateForUser_ShouldMergeAttributes()
        {
            // Arrange
            var baseContext = new Context(new { environment = "production" }) { ClientKey = "test-key" };
            using var factory = new GrowthBookFactory(baseContext);

            // Act
            using var growthBook = factory.CreateForUser(new { userId = "user123", role = "admin" });

            // Assert
            growthBook.Attributes["environment"].ToString().Should().Be("production");
            growthBook.Attributes["userId"].ToString().Should().Be("user123");
            growthBook.Attributes["role"].ToString().Should().Be("admin");
        }

        [Fact]
        public void GrowthBookFactory_CreateForUser_WithNullAttributes_ShouldUseBaseContextOnly()
        {
            // Arrange
            var baseContext = new Context(new { environment = "test" }) { ClientKey = "test-key" };
            using var factory = new GrowthBookFactory(baseContext);

            // Act
            using var growthBook = factory.CreateForUser((object)null);

            // Assert
            growthBook.Attributes["environment"].ToString().Should().Be("test");
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}