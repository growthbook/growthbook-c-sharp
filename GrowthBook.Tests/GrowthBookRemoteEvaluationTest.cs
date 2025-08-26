using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook;
using GrowthBook.Api;
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
    }
}
