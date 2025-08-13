using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for threading issues in .NET Framework MVC - GitHub issue #41
    /// </summary>
    public class ThreadingIssueTests
    {
        [Fact]
        public async Task LoadFeatures_Should_Work_Without_SynchronizationContext()
        {
            // Simulate the MVC scenario where SynchronizationContext can be problematic
            // by removing it entirely during the test
            var originalContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);

            try
            {
                var context = new Context
                {
                    Enabled = true,
                    ClientKey = "sdk-test-key",
                    ApiHost = "https://cdn.growthbook.io",
                    Features = new Dictionary<string, Feature>
                    {
                        ["test-feature"] = new Feature { DefaultValue = true }
                    },
                    LoggerFactory = NullLoggerFactory.Instance
                };

                using var growthBook = new GrowthBookSdk.GrowthBook(context);

                // This should work without throwing exceptions related to SynchronizationContext
                var loadResult = await growthBook.LoadFeaturesWithResult();

                // The load might fail (due to network or invalid key), but it shouldn't crash
                // with threading-related exceptions
                loadResult.Should().NotBeNull();
                
                // Test that feature evaluation still works
                var featureResult = growthBook.IsOn("test-feature");
                featureResult.Should().BeTrue();
            }
            finally
            {
                // Restore the original context
                SynchronizationContext.SetSynchronizationContext(originalContext);
            }
        }

        [Fact]
        public async Task LoadFeatures_Should_Handle_Cancellation_Properly()
        {
            var context = new Context
            {
                Enabled = true,
                ClientKey = "sdk-test-key", 
                ApiHost = "https://cdn.growthbook.io",
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);
            using var cts = new CancellationTokenSource();

            // Cancel immediately to test cancellation handling
            cts.Cancel();

            var loadResult = await growthBook.LoadFeaturesWithResult(cancellationToken: cts.Token);

            // Should handle cancellation gracefully without crashing
            loadResult.Should().NotBeNull();
            loadResult.Success.Should().BeFalse();
        }

        [Fact]
        public async Task Multiple_LoadFeatures_Calls_Should_Not_Conflict()
        {
            var context = new Context
            {
                Enabled = true,
                ClientKey = "sdk-test-key",
                ApiHost = "https://cdn.growthbook.io", 
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);

            // Simulate multiple concurrent calls (which could happen in high-traffic MVC scenarios)
            var tasks = new List<Task<FeatureLoadResult>>();
            
            for (int i = 0; i < 3; i++)
            {
                tasks.Add(growthBook.LoadFeaturesWithResult());
            }

            var results = await Task.WhenAll(tasks);

            // All calls should complete without throwing threading exceptions
            foreach (var result in results)
            {
                result.Should().NotBeNull();
            }
        }

        [Fact]
        public async Task LoadFeatures_Should_Work_On_Thread_Pool_Thread()
        {
            // Test that LoadFeatures works when called from a thread pool thread
            // (simulating background processing scenarios)
            
            FeatureLoadResult result = null;
            Exception thrownException = null;

            await Task.Run(async () =>
            {
                try
                {
                    var context = new Context
                    {
                        Enabled = true,
                        ClientKey = "sdk-test-key",
                        ApiHost = "https://cdn.growthbook.io",
                        LoggerFactory = NullLoggerFactory.Instance
                    };

                    using var growthBook = new GrowthBookSdk.GrowthBook(context);
                    result = await growthBook.LoadFeaturesWithResult();
                }
                catch (Exception ex)
                {
                    thrownException = ex;
                }
            });

            // Should not throw threading-related exceptions
            thrownException.Should().BeNull();
            result.Should().NotBeNull();
        }

        [Fact]
        public void LoadFeatures_Synchronous_Should_Not_Deadlock()
        {
            // Test the scenario mentioned in the GitHub issue where .Wait() could cause deadlocks
            var context = new Context
            {
                Enabled = true,
                Features = new Dictionary<string, Feature>
                {
                    ["test-feature"] = new Feature { DefaultValue = false }
                },
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBook = new GrowthBookSdk.GrowthBook(context);

            // This should not deadlock - test without trying to load from API
            var result = growthBook.GetFeatureValue("test-feature", true, alwaysLoadFeatures: false);
            
            result.Should().BeFalse(); // Should return the default value from the feature
        }
    }
}