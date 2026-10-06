using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Api;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests
{
    public class GrowthBookRemoteEvaluationTests
    {
        [Fact]
        public void Constructor_WithValidRemoteEvalConfig_ShouldNotThrow()
        {
            // Arrange & Act & Assert
            var context = new Context
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com"
            };

            var growthBook = new GrowthBook(context);
            growthBook.Should().NotBeNull();
        }

        [Fact]
        public void Constructor_WithInvalidConfig_ShouldThrowArgumentException()
        {
            // Missing ClientKey
            var contextMissingKey = new Context { RemoteEval = true, ApiHost = "https://api.example.com" };
            Assert.Throws<ArgumentException>(() => new GrowthBook(contextMissingKey));

            // Missing ApiHost  
            var contextMissingHost = new Context { RemoteEval = true, ClientKey = "test-key" };
            Assert.Throws<ArgumentException>(() => new GrowthBook(contextMissingHost));

            // With DecryptionKey
            var contextWithDecryption = new Context
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                DecryptionKey = "key"
            };
            Assert.Throws<ArgumentException>(() => new GrowthBook(contextWithDecryption));
        }

        [Fact]
        public async Task LoadFeaturesWithResult_ShouldUseCorrectMethod()
        {
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<System.Threading.CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));
            mockRepository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<System.Threading.CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            // Test with RemoteEval enabled
            var remoteContext = new Context
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };
            var remoteGrowthBook = new GrowthBook(remoteContext);
            await remoteGrowthBook.LoadFeaturesWithResult();
            await mockRepository.Received(1).GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<System.Threading.CancellationToken?>());

            // Test with RemoteEval disabled
            var regularContext = new Context { RemoteEval = false, FeatureRepository = mockRepository };
            var regularGrowthBook = new GrowthBook(regularContext);
            await regularGrowthBook.LoadFeaturesWithResult();
            await mockRepository.Received(1).GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<System.Threading.CancellationToken?>());
        }

        [Fact]
        public void UpdateAttributes_ShouldUpdateCorrectly()
        {
            var context = new Context();
            var growthBook = new GrowthBook(context);

            // Test UpdateAttributes
            growthBook.UpdateAttributes(new { userId = "123" });
            growthBook.Attributes["userId"].ToString().Should().Be("123");

            // Test MergeAttributes
            growthBook.MergeAttributes(new { plan = "premium" });
            growthBook.Attributes["userId"].ToString().Should().Be("123");
            growthBook.Attributes["plan"].ToString().Should().Be("premium");
        }

        [Fact]
        public async Task MergeAttributesAsync_ShouldWaitForTheRemoteEvaluationOfTheMergedAttributes()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            await growthBook.MergeAttributesAsync(new { plan = "premium" });

            // Assert
            await mockRepository.Received(1).GetFeaturesWithContext(
                Arg.Is<Context>(x => x.Attributes["userId"].ToString() == "123" && x.Attributes["plan"].ToString() == "premium"),
                Arg.Any<GrowthBookRetrievalOptions>(),
                Arg.Any<CancellationToken?>());

            growthBook.Features.Should().ContainKey("test");
        }

        [Fact]
        public async Task MergeAttributesAsync_WithoutAnActualChange_ShouldNotTriggerRemoteEvaluation()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            await growthBook.MergeAttributesAsync(new { userId = "123" });

            // Assert
            await mockRepository.DidNotReceive().GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>());
        }

        [Fact]
        public async Task LoadFeaturesWithResult_ShouldWaitForAPendingRemoteEvaluation()
        {
            // Arrange
            var pendingRemoteEvaluation = new TaskCompletionSource<IDictionary<string, Feature>>();
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(pendingRemoteEvaluation.Task, Task.FromResult<IDictionary<string, Feature>>(features));

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.MergeAttributes(new { plan = "premium" });

            var load = growthBook.LoadFeaturesWithResult();

            // Assert
            load.IsCompleted.Should().BeFalse(); // Blocked on the remote evaluation that the merge started

            pendingRemoteEvaluation.SetResult(features);

            var result = await load;

            result.Success.Should().BeTrue();

            await mockRepository.Received(2).GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>());
        }

        [Fact]
        public async Task RemoteEvaluation_WhenAnOlderResponseLandsLast_ShouldNotOverwriteTheNewerOne()
        {
            // Arrange
            var olderResponse = new TaskCompletionSource<IDictionary<string, Feature>>();
            var newerResponse = new TaskCompletionSource<IDictionary<string, Feature>>();

            var olderFeatures = new Dictionary<string, Feature> { { "older", new Feature { DefaultValue = true } } };
            var newerFeatures = new Dictionary<string, Feature> { { "newer", new Feature { DefaultValue = true } } };

            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(olderResponse.Task, newerResponse.Task);

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act - two attribute changes in a row leave two independent evaluations in flight
            var older = growthBook.MergeAttributesAsync(new { plan = "premium" });
            var newer = growthBook.MergeAttributesAsync(new { plan = "enterprise" });

            newerResponse.SetResult(newerFeatures);
            await newer;

            // The evaluation made for the attributes that have already been replaced completes last
            olderResponse.SetResult(olderFeatures);
            await older;

            // Assert
            growthBook.Features.Should().ContainKey("newer");
            growthBook.Features.Should().NotContainKey("older");
        }

        [Fact]
        public async Task LoadFeaturesWithResult_WhenAnAttributeChangeIsEvaluatedFirst_ShouldNotOverwriteIt()
        {
            // Arrange
            var loadResponse = new TaskCompletionSource<IDictionary<string, Feature>>();
            var mergeResponse = new TaskCompletionSource<IDictionary<string, Feature>>();

            var olderFeatures = new Dictionary<string, Feature> { { "older", new Feature { DefaultValue = true } } };
            var newerFeatures = new Dictionary<string, Feature> { { "newer", new Feature { DefaultValue = true } } };

            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(loadResponse.Task, mergeResponse.Task);

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act - the attributes change while the load is still waiting on its own evaluation
            var load = growthBook.LoadFeaturesWithResult();
            var merge = growthBook.MergeAttributesAsync(new { plan = "premium" });

            mergeResponse.SetResult(newerFeatures);
            await merge;

            loadResponse.SetResult(olderFeatures);
            var result = await load;

            // Assert
            result.Success.Should().BeTrue();
            growthBook.Features.Should().ContainKey("newer");
            growthBook.Features.Should().NotContainKey("older");
        }

        [Fact]
        public async Task SetForcedVariationsAsync_ShouldWaitForTheRemoteEvaluationOfTheNewForcedVariations()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            await growthBook.SetForcedVariationsAsync(new Dictionary<string, int> { { "my-experiment", 1 } });

            // Assert - forced variations are part of the payload, so the change has to be evaluated again
            await mockRepository.Received(1).GetFeaturesWithContext(
                Arg.Is<Context>(x => x.ForcedVariations["my-experiment"] == 1),
                Arg.Any<GrowthBookRetrievalOptions>(),
                Arg.Any<CancellationToken?>());

            growthBook.Features.Should().ContainKey("test");
        }

        [Fact]
        public async Task SetForcedVariationsAsync_WithoutAnActualChange_ShouldNotTriggerRemoteEvaluation()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                ForcedVariations = new Dictionary<string, int> { { "my-experiment", 1 } },
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            await growthBook.SetForcedVariationsAsync(new Dictionary<string, int> { { "my-experiment", 1 } });

            // Assert
            await mockRepository.DidNotReceive().GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>());
        }

        /// <summary>
        /// A forced value short-circuits evaluation of its own feature locally, so the obvious reading is that
        /// the server never needs to know. It does: a feature that reaches a forced one through a prerequisite
        /// is resolved server-side, and an override applied locally afterwards cannot correct that result.
        /// </summary>
        [Fact]
        public async Task SetForcedFeaturesAsync_ShouldSendTheForcedValuesToBeEvaluatedAgainst()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            await growthBook.SetForcedFeaturesAsync(new Dictionary<string, JToken> { { "dark-mode", JToken.FromObject(true) } });

            // Assert
            await mockRepository.Received(1).GetFeaturesWithContext(
                Arg.Is<Context>(x => x.ForcedFeatures != null
                    && x.ForcedFeatures.ContainsKey("dark-mode")
                    && x.ForcedFeatures["dark-mode"].Value<bool>()),
                Arg.Any<GrowthBookRetrievalOptions>(),
                Arg.Any<CancellationToken?>());

            growthBook.Features.Should().ContainKey("test");
        }

        [Fact]
        public async Task SetForcedFeaturesAsync_WithoutAnActualChange_ShouldNotTriggerRemoteEvaluation()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                ForcedFeatures = new Dictionary<string, JToken> { { "dark-mode", JToken.FromObject(true) } },
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act - the same value, built as a separate token, so this leans on a structural comparison
            await growthBook.SetForcedFeaturesAsync(new Dictionary<string, JToken> { { "dark-mode", JToken.FromObject(true) } });

            // Assert
            await mockRepository.DidNotReceive().GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>());
        }

        /// <summary>
        /// Evaluating locally means no request to make stale, so forcing a feature must stay as cheap as it was.
        /// </summary>
        [Fact]
        public void SetForcedFeatures_WithoutRemoteEval_ShouldNotTriggerAnything()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();

            var context = new Context(new { userId = "123" })
            {
                Features = new Dictionary<string, Feature> { { "dark-mode", new Feature { DefaultValue = false } } },
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act
            growthBook.SetForcedFeatures(new Dictionary<string, JToken> { { "dark-mode", JToken.FromObject(true) } });

            // Assert
            growthBook.IsOn("dark-mode").Should().BeTrue("because the override still short-circuits evaluation locally");
            mockRepository.DidNotReceiveWithAnyArgs().GetFeaturesWithContext(default, default, default);
        }

        [Fact]
        public async Task Attributes_WhenAssignedDirectly_ShouldStartARemoteEvaluation()
        {
            // Arrange
            var mockRepository = Substitute.For<IGrowthBookFeatureRepository>();
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };

            mockRepository.GetFeaturesWithContext(Arg.Any<Context>(), Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken?>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

            var context = new Context(new { userId = "123" })
            {
                RemoteEval = true,
                ClientKey = "test-key",
                ApiHost = "https://api.example.com",
                FeatureRepository = mockRepository
            };

            using var growthBook = new GrowthBook(context);

            // Act - assigning the property replaces the attributes, the same as UpdateAttributes
            growthBook.Attributes = JObject.FromObject(new { userId = "456" });

            // Assert
            await mockRepository.Received(1).GetFeaturesWithContext(
                Arg.Is<Context>(x => x.Attributes["userId"].ToString() == "456"),
                Arg.Any<GrowthBookRetrievalOptions>(),
                Arg.Any<CancellationToken?>());
        }
    }
}
