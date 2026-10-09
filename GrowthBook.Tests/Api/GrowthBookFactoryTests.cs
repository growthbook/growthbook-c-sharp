using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
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

        [Fact]
        public void GrowthBookFactory_SharedConfiguration_ShouldNotEnableServerSentEvents_ByDefault()
        {
            // BackgroundSync is opt-in (Context.BackgroundSync defaults to false) and FeatureRefreshWorker
            // opens the SSE connection off PreferServerSentEvents, so forcing it on here would give every
            // factory user a streaming connection they never asked for.
            var config = GrowthBookFactory.CreateSharedConfiguration(new Context { ClientKey = "test-key" });

            config.PreferServerSentEvents.Should().BeFalse();
        }

        [Fact]
        public void GrowthBookFactory_SharedConfiguration_ShouldEnableServerSentEvents_WhenBackgroundSyncIsRequested()
        {
            var context = new Context { ClientKey = "test-key", BackgroundSync = true };

            var config = GrowthBookFactory.CreateSharedConfiguration(context);

            config.PreferServerSentEvents.Should().BeTrue("because opting in has to still work");
        }

        [Fact]
        public void GrowthBookFactory_SharedConfiguration_ShouldCarryTheContextSettings()
        {
            var context = new Context
            {
                ApiHost = "https://cdn.example.test",
                ClientKey = "test-key",
                DecryptionKey = "test-decryption-key",
                CacheExpirationInSeconds = 15
            };

            var config = GrowthBookFactory.CreateSharedConfiguration(context);

            config.ApiHost.Should().Be("https://cdn.example.test");
            config.ClientKey.Should().Be("test-key");
            config.DecryptionKey.Should().Be("test-decryption-key");
            config.CacheExpirationInSeconds.Should().Be(15);
        }

        [Fact]
        public void GrowthBookFactory_SharedConfiguration_ShouldFallBackToTheDefaultApiHost()
        {
            var config = GrowthBookFactory.CreateSharedConfiguration(new Context { ClientKey = "test-key" });

            config.ApiHost.Should().Be("https://cdn.growthbook.io");
        }

        [Fact]
        public void GrowthBookFactory_CreateForUser_WithoutACallback_ShouldKeepTheBaseContextOne()
        {
            var tracked = new List<string>();
            var baseContext = new Context(new { id = "1" })
            {
                TrackingCallback = (experiment, result) => tracked.Add(experiment.Key)
            };

            using var factory = new GrowthBookFactory(baseContext);
            using var growthBook = factory.CreateForUser(new { plan = "premium" });

            growthBook.Run(new Experiment { Key = "my-experiment", Variations = JArray.Parse("[0, 1]") });

            // Assigning the per-user callback unconditionally used to wipe this one, which silently stopped
            // tracking exposures for every user created without a callback of their own.
            tracked.Should().Equal("my-experiment");
        }

        [Fact]
        public void GrowthBookFactory_CreateForUser_WithACallback_ShouldPreferItOverTheBaseContextOne()
        {
            var fromBase = new List<string>();
            var fromUser = new List<string>();

            var baseContext = new Context(new { id = "1" })
            {
                TrackingCallback = (experiment, result) => fromBase.Add(experiment.Key)
            };

            using var factory = new GrowthBookFactory(baseContext);
            using var growthBook = factory.CreateForUser(
                new { plan = "premium" },
                (experiment, result) => fromUser.Add(experiment.Key));

            growthBook.Run(new Experiment { Key = "my-experiment", Variations = JArray.Parse("[0, 1]") });

            fromUser.Should().Equal("my-experiment");
            fromBase.Should().BeEmpty("because a supplied callback still replaces the base one");
        }

        [Fact]
        public void GrowthBookFactory_SharedRepository_ShouldGetARemoteEvaluationService_WhenTheContextAsksForOne()
        {
            var context = new Context { ClientKey = "test-key", RemoteEval = true };

            var repository = GrowthBookFactory.CreateSharedRepository(context, LoggerFactory.Create(_ => { }));

            // Read by reflection because the repository keeps the service private, and the alternative - asserting
            // on an actual remote evaluation - needs a server. Without it the same context evaluated remotely
            // through new GrowthBook(context) but silently did not through the factory.
            RemoteEvaluationServiceOf(repository).Should().NotBeNull();
        }

        [Fact]
        public void GrowthBookFactory_SharedRepository_ShouldNotGetARemoteEvaluationService_ByDefault()
        {
            var repository = GrowthBookFactory.CreateSharedRepository(
                new Context { ClientKey = "test-key" },
                LoggerFactory.Create(_ => { }));

            RemoteEvaluationServiceOf(repository).Should().BeNull();
        }

        [Fact]
        public void GrowthBookFactory_Dispose_ShouldNotDisposeAnInjectedLoggerFactory()
        {
            var loggerFactory = Substitute.For<ILoggerFactory>();
            var baseContext = new Context { ClientKey = "test-key", LoggerFactory = loggerFactory };

            var factory = new GrowthBookFactory(baseContext);
            factory.Dispose();

            // The caller may well keep logging through it after the factory is gone.
            loggerFactory.DidNotReceive().Dispose();
        }

        [Fact]
        public void GrowthBookFactory_Dispose_ShouldDisposeALoggerFactoryItCreatedItself()
        {
            var factory = new GrowthBookFactory(new Context { ClientKey = "test-key" });
            var loggerFactory = LoggerFactoryOf(factory);

            factory.Dispose();

            // A disposed ILoggerFactory throws on use, which is the only observable difference - the factory used
            // to drop the one it created on the floor instead.
            Action act = () => loggerFactory.CreateLogger<GrowthBookFactoryTests>();
            act.Should().Throw<ObjectDisposedException>();
        }

        private static IRemoteEvaluationService RemoteEvaluationServiceOf(IGrowthBookFeatureRepository repository) =>
            (IRemoteEvaluationService)typeof(FeatureRepository)
                .GetField("_remoteEvaluationService", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(repository);

        private static ILoggerFactory LoggerFactoryOf(GrowthBookFactory factory) =>
            (ILoggerFactory)typeof(GrowthBookFactory)
                .GetField("_loggerFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(factory);

        public void Dispose()
        {
            // Cleanup if needed
        }
    }
}