using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using GrowthBook.Extensions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.Api
{
    /// <summary>
    /// Basic tests for improved Attributes API with IDictionary and anonymous object support.
    /// </summary>
    public class AttributesApiTests : IDisposable
    {
        [Fact]
        public void Context_WithAnonymousObject_ShouldSetAttributes()
        {
            // Arrange & Act
            var context = new Context(new { userId = "user123", age = 25 });

            // Assert
            context.Attributes["userId"].ToString().Should().Be("user123");
            context.Attributes["age"].ToObject<int>().Should().Be(25);
        }

        [Fact]
        public void Context_SetAttributes_WithIDictionary_ShouldUpdateAttributes()
        {
            // Arrange
            var context = new Context();
            var attributes = new Dictionary<string, object>
            {
                ["userId"] = "user789",
                ["role"] = "admin"
            };

            // Act
            context.SetAttributes(attributes);

            // Assert
            context.Attributes["userId"].ToString().Should().Be("user789");
            context.Attributes["role"].ToString().Should().Be("admin");
        }

        [Fact]
        public void GrowthBook_UpdateAttributes_ShouldReplaceAttributes()
        {
            // Arrange
            var context = new Context(new { userId = "original" });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.UpdateAttributes(new { userId = "updated", role = "admin" });

            // Assert
            growthBook.Attributes["userId"].ToString().Should().Be("updated");
            growthBook.Attributes["role"].ToString().Should().Be("admin");
        }

        [Fact]
        public void GrowthBook_MergeAttributes_ShouldMergeAttributes()
        {
            // Arrange
            var context = new Context(new { userId = "user123", age = 25 });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.MergeAttributes(new { role = "admin", age = 30 });

            // Assert
            growthBook.Attributes["userId"].ToString().Should().Be("user123"); // Preserved
            growthBook.Attributes["role"].ToString().Should().Be("admin"); // Added
            growthBook.Attributes["age"].ToObject<int>().Should().Be(30); // Overwritten
        }

        [Fact]
        public void GrowthBookFactory_CreateForUser_ShouldCreateInstanceWithMergedAttributes()
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

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}