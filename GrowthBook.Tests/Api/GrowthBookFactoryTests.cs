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

        [Fact]
        public void GrowthBookFactory_CreateForUser_KeepsEachUsersFeaturesIsolated()
        {
            // The per-user clone shares the Features dictionary with the base context to avoid copying it
            // twice, relying on the GrowthBook constructor to do the isolating copy. This pins that the
            // isolation actually still happens - mutating one user's Features must not leak anywhere else.
            const string FeatureName = "shared-feature";

            var baseContext = new Context
            {
                ClientKey = "test-key",
                Features = new Dictionary<string, Feature>
                {
                    [FeatureName] = new Feature { DefaultValue = true }
                }
            };
            using var factory = new GrowthBookFactory(baseContext);

            using var firstUser = factory.CreateForUser(new { userId = "user-1" });
            using var secondUser = factory.CreateForUser(new { userId = "user-2" });

            firstUser.Features[FeatureName] = new Feature { DefaultValue = false };

            secondUser.IsOn(FeatureName).Should().BeTrue("because one user's feature mutation must not leak into another user's instance");
            baseContext.Features[FeatureName].DefaultValue.ToObject<bool>().Should().BeTrue("because it must not leak back into the base context either");
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}