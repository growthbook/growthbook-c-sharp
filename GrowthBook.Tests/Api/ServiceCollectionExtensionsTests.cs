using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using GrowthBook;
using GrowthBook.Extensions;

namespace GrowthBook.Tests.Api
{
    public class ServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddGrowthBook_WithConfigureContext_RegistersFactoryAndScopedClient()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddGrowthBook(ctx =>
            {
                ctx.ApiHost = "https://cdn.growthbook.io";
                ctx.ClientKey = "test-key";
                ctx.Enabled = true;
                ctx.Attributes = new Newtonsoft.Json.Linq.JObject();
            });

            var provider = services.BuildServiceProvider();

            // Act
            var factory1 = provider.GetRequiredService<GrowthBookFactory>();
            var factory2 = provider.GetRequiredService<GrowthBookFactory>();

            IGrowthBook scoped1;
            IGrowthBook scoped2;

            using (var scope = provider.CreateScope())
            {
                scoped1 = scope.ServiceProvider.GetRequiredService<IGrowthBook>();
            }

            using (var scope = provider.CreateScope())
            {
                scoped2 = scope.ServiceProvider.GetRequiredService<IGrowthBook>();
            }

            // Assert
            factory1.Should().NotBeNull();
            ReferenceEquals(factory1, factory2).Should().BeTrue("factory should be singleton");

            scoped1.Should().NotBeNull();
            scoped2.Should().NotBeNull();
            ReferenceEquals(scoped1, scoped2).Should().BeFalse("IGrowthBook should be scoped");
        }

        [Fact]
        public void AddGrowthBook_WithBaseContext_RegistersSuccessfully()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging();

            var baseContext = new Context
            {
                ApiHost = "https://cdn.growthbook.io",
                ClientKey = "base-key",
                Enabled = true,
                Attributes = new Newtonsoft.Json.Linq.JObject()
            };

            services.AddGrowthBook(baseContext);

            var provider = services.BuildServiceProvider();

            // Act
            var factory = provider.GetRequiredService<GrowthBookFactory>();
            IGrowthBook scoped;

            using (var scope = provider.CreateScope())
            {
                scoped = scope.ServiceProvider.GetRequiredService<IGrowthBook>();
            }

            // Assert
            factory.Should().NotBeNull();
            scoped.Should().NotBeNull();
        }

        [Fact]
        public void Factory_CreateForUser_CreatesDistinctInstances()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddGrowthBook(ctx =>
            {
                ctx.ApiHost = "https://cdn.growthbook.io";
                ctx.ClientKey = "test-key";
                ctx.Enabled = true;
                ctx.Attributes = new Newtonsoft.Json.Linq.JObject();
            });

            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<GrowthBookFactory>();

            // Act
            var gb1 = factory.CreateForUser(new Dictionary<string, object> { ["userId"] = "1" });
            var gb2 = factory.CreateForUser(new Dictionary<string, object> { ["userId"] = "2" });

            // Assert
            ReferenceEquals(gb1, gb2).Should().BeFalse("factory should produce new instances per user");
            gb1.Dispose();
            gb2.Dispose();
        }
    }
}
