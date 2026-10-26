using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Extensions;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.Api;

public class ServiceCollectionExtensionsTests
{
    private static IDictionary<string, Feature> CreateFeatures() => new Dictionary<string, Feature>
    {
        ["dark-mode"] = new Feature { DefaultValue = JToken.FromObject(true) }
    };

    private static IGrowthBookFeatureRepository CreateRepository()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => LoadFeaturesAsynchronously());

        return repository;
    }

    /// <summary>
    /// Completes on another thread rather than synchronously. A repository that returns an already
    /// finished task posts no continuation anywhere, so it cannot show a deadlock even when there is
    /// one - which is what the first version of the test below did.
    /// </summary>
    private static async Task<IDictionary<string, Feature>> LoadFeaturesAsynchronously()
    {
        await Task.Delay(30).ConfigureAwait(false);

        return CreateFeatures();
    }

    [Fact]
    public void TheClientIsRegisteredAsASingleton()
    {
        var services = new ServiceCollection();

        services.AddGrowthBookClient(options =>
        {
            options.ClientKey = "sdk-test";
            options.LoggerFactory = NullLoggerFactory.Instance;
            options.FeatureRepository = CreateRepository();
        });

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<GrowthBookClient>();
        var second = provider.GetRequiredService<GrowthBookClient>();

        first.Should().BeSameAs(second, "one client serves every user in the process");
    }

    /// <summary>
    /// Resolving the client has to leave it usable. Loading lazily on the first evaluation would race
    /// the first request instead.
    /// </summary>
    [Fact]
    public void TheClientHasItsFeaturesBeforeTheContainerHandsItOver()
    {
        var services = new ServiceCollection();

        services.AddGrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = CreateRepository()
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<GrowthBookClient>()
            .GetFeatures().Should().ContainKey("dark-mode");
    }

    /// <summary>
    /// A container resolves a singleton on whichever thread asked for it first. Blocking there while
    /// the load's continuation waits for that same thread is a deadlock, so the load is started off it.
    /// </summary>
    [Fact]
    public void ResolvingUnderASingleThreadedContextDoesNotDeadlock()
    {
        var services = new ServiceCollection();

        services.AddGrowthBookClient(options =>
        {
            options.ClientKey = "sdk-test";
            options.LoggerFactory = NullLoggerFactory.Instance;
            options.FeatureRepository = CreateRepository();
        });

        using var provider = services.BuildServiceProvider();

        var finished = RunUnderSingleThreadedContext(() =>
        {
            provider.GetRequiredService<GrowthBookClient>().GetFeatures().Should().ContainKey("dark-mode");
        });

        finished.Should().BeTrue("resolving the client must not wait on the thread doing the resolving");
    }

    [Fact]
    public void RegisteringWithoutServicesOrOptionsIsRejected()
    {
        var services = new ServiceCollection();

        services.Invoking(s => s.AddGrowthBookClient((Options)null)).Should().Throw<ArgumentNullException>();
        services.Invoking(s => s.AddGrowthBookClient((Action<Options>)null)).Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Runs the work on a thread that pumps its own queue, the way a desktop UI thread or classic
    /// ASP.NET request thread does, and reports whether it finished.
    /// </summary>
    private static bool RunUnderSingleThreadedContext(Action work)
    {
        var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var context = new SingleThreadedSynchronizationContext();

            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                work();
                finished.Set();
            }
            finally
            {
                context.Stop();
            }
        });

        thread.IsBackground = true;
        thread.Start();

        var completed = finished.Wait(TimeSpan.FromSeconds(10));

        thread.Join(TimeSpan.FromSeconds(1));

        return completed;
    }

    private sealed class SingleThreadedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object State)> _queue =
            new System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object)>();

        public override void Post(SendOrPostCallback d, object state) => _queue.Add((d, state));

        public void Stop() => _queue.CompleteAdding();
    }
}
