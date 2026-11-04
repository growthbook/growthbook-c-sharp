using System;
using System.Collections.Generic;
using System.Reflection;
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
        public void GrowthBookFactory_CreateForUser_PropagatesApiHeadersAndStreamingHostConfiguration()
        {
            // Arrange
            var baseContext = new Context
            {
                ClientKey = "test-key",
                ApiHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer factory-token" },
                StreamingHost = "https://streaming.example.com",
                StreamingHostRequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer streaming-token" }
            };
            using var factory = new GrowthBookFactory(baseContext);

            // Act
            using var growthBook = factory.CreateForUser(new { userId = "user123" });

            // Assert
            var contextField = typeof(GrowthBook).GetField("_context", BindingFlags.NonPublic | BindingFlags.Instance);
            var context = (Context)contextField.GetValue(growthBook);

            context.ApiHostRequestHeaders["Authorization"].Should().Be("Bearer factory-token");
            context.StreamingHost.Should().Be("https://streaming.example.com");
            context.StreamingHostRequestHeaders["Authorization"].Should().Be("Bearer streaming-token");
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}