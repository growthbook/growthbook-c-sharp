using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        public void GrowthBook_MergeAttributes_WithDictionary_ShouldMergeAttributes()
        {
            // Arrange
            var context = new Context(new { userId = "user123", age = 25 });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.MergeAttributes(new Dictionary<string, object>
            {
                ["role"] = "admin",
                ["age"] = 30
            });

            // Assert
            growthBook.Attributes["userId"].ToString().Should().Be("user123"); // Preserved
            growthBook.Attributes["role"].ToString().Should().Be("admin"); // Added
            growthBook.Attributes["age"].ToObject<int>().Should().Be(30); // Overwritten
        }

        [Fact]
        public void GrowthBook_MergeAttributes_WithNullValue_ShouldStoreJsonNullAndKeepTheKey()
        {
            // Arrange
            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.MergeAttributes(new Dictionary<string, object> { ["userId"] = null, ["role"] = null });

            // Assert
            growthBook.Attributes.ContainsKey("userId").Should().BeTrue();
            growthBook.Attributes["userId"].Type.Should().Be(JTokenType.Null);
            growthBook.Attributes.ContainsKey("role").Should().BeTrue();
            growthBook.Attributes["role"].Type.Should().Be(JTokenType.Null);
        }

        [Fact]
        public void GrowthBook_MergeAttributes_WithNullArgument_ShouldBeNoOp()
        {
            // Arrange
            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.MergeAttributes((IDictionary<string, object>)null);
            growthBook.MergeAttributes((object)null);

            // Assert
            growthBook.Attributes["userId"].ToString().Should().Be("user123");
            growthBook.Attributes.Count.Should().Be(1);
        }

        [Fact]
        public void GrowthBook_UpdateAttributes_WithNullArgument_ShouldClearAttributes()
        {
            // Arrange
            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.UpdateAttributes((IDictionary<string, object>)null);

            // Assert
            growthBook.Attributes.Count.Should().Be(0);
        }

        [Fact]
        public void GrowthBook_MergeAttributes_ShouldSwapInANewInstanceInsteadOfMutatingTheCurrentOne()
        {
            // Arrange
            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);
            var attributesBeforeMerge = growthBook.Attributes;

            // Act
            growthBook.MergeAttributes(new { role = "admin" });

            // Assert
            growthBook.Attributes.Should().NotBeSameAs(attributesBeforeMerge);
            attributesBeforeMerge.ContainsKey("role").Should().BeFalse(); // The snapshot a concurrent evaluation holds is untouched
            growthBook.Attributes["role"].ToString().Should().Be("admin");
        }

        [Fact]
        public void GrowthBook_MergeAttributes_WithJObject_ShouldNotTakeOwnershipOfTheCallersInstance()
        {
            // Arrange
            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);
            var additionalAttributes = new JObject { ["role"] = "admin" };

            // Act
            growthBook.MergeAttributes(additionalAttributes);
            additionalAttributes["role"] = "changed-after-the-merge";

            // Assert
            growthBook.Attributes["role"].ToString().Should().Be("admin");
        }

        [Fact]
        public async Task GrowthBook_MergeAttributes_ShouldBeSafeUnderConcurrentUpdatesAndReads()
        {
            // Arrange
            const int MergeCount = 50;

            var context = new Context(new { userId = "user123" });
            using var growthBook = new GrowthBook(context);

            // Act
            var merges = Enumerable
                .Range(0, MergeCount)
                .Select(x => Task.Run(() => growthBook.MergeAttributes(new Dictionary<string, object> { [$"key{x}"] = x })));

            var reads = Enumerable
                .Range(0, MergeCount)
                .Select(x => Task.Run(() =>
                {
                    for (var i = 0; i < 100; i++)
                    {
                        // Each read takes the current snapshot and enumerates it, which will throw
                        // if attributes are being mutated in place while an evaluation is reading them.
                        foreach (var property in growthBook.Attributes.Properties())
                        {
                            _ = property.Name;
                        }
                    }
                }));

            await Task.WhenAll(merges.Concat(reads));

            // Assert
            growthBook.Attributes["userId"].ToString().Should().Be("user123");
            growthBook.Attributes.Count.Should().Be(MergeCount + 1);

            for (var i = 0; i < MergeCount; i++)
            {
                growthBook.Attributes[$"key{i}"].ToObject<int>().Should().Be(i);
            }
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