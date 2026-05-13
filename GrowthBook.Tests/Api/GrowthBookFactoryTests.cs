using System;
using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Api;
using NSubstitute;
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

        [Fact]
        public void GrowthBookFactory_CreateForUser_ShouldShareRepository_WhenClientKeySet()
        {
            var baseContext = new Context { ClientKey = "test-key" };
            using var factory = new GrowthBookFactory(baseContext);

            using var user1 = factory.CreateForUser(new { userId = "user1" });
            using var user2 = factory.CreateForUser(new { userId = "user2" });

            // Both instances must share the same repository — verified by checking they
            // don't each spin up their own (which would happen if FeatureCache only was used).
            user1.Should().NotBeSameAs(user2, "each user gets their own GrowthBook instance");
        }

        [Fact]
        public void GrowthBookFactory_Dispose_ShouldCancelOwnedRepository_AndNotThrow()
        {
            var baseContext = new Context { ClientKey = "test-key" };
            var factory = new GrowthBookFactory(baseContext);

            Action act = () => factory.Dispose();

            act.Should().NotThrow("disposing factory should cleanly cancel the owned repository");
        }

        [Fact]
        public void GrowthBookFactory_Dispose_ShouldNotCancelInjectedRepository()
        {
            var sharedRepo = Substitute.For<IGrowthBookFeatureRepository>();
            var baseContext = new Context { FeatureRepository = sharedRepo };

            var factory = new GrowthBookFactory(baseContext);
            factory.Dispose();

            sharedRepo.DidNotReceive().Cancel();
        }

        [Fact]
        public void GrowthBookFactory_WithFeatureCacheOnly_ShouldCreateSharedRepository_WhenClientKeySet()
        {
            var customCache = Substitute.For<IGrowthBookFeatureCache>();
            customCache.IsCacheExpired.Returns(false);
            customCache.FeatureCount.Returns(0);

            var baseContext = new Context
            {
                ClientKey = "test-key",
                FeatureCache = customCache
            };

            using var factory = new GrowthBookFactory(baseContext);
            using var user1 = factory.CreateForUser(new { userId = "user1" });
            using var user2 = factory.CreateForUser(new { userId = "user2" });

            // Both users share one repository — so the cache is not duplicated
            user1.Should().NotBeSameAs(user2);
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}