using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.Exceptions;
using GrowthBook.Extensions;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// A feature API that is down at startup must not take the application down with it. Letting the
    /// exception escape would, and worse: a container does not cache a singleton whose factory threw,
    /// so every later resolution builds another client and blocks on another load.
    /// </summary>
    [Fact]
    public void AFailedFirstLoadStillYieldsOneUsableClient()
    {
        var attempts = 0;
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);

                return Task.FromException<IDictionary<string, Feature>>(
                    new FeatureLoadException("the features api is unreachable", 503));
            });

        var services = new ServiceCollection();

        services.AddGrowthBookClient(options =>
        {
            options.ClientKey = "sdk-test";
            options.LoggerFactory = NullLoggerFactory.Instance;
            options.FeatureRepository = repository;
        });

        using var provider = services.BuildServiceProvider();

        var first = provider.Invoking(p => p.GetRequiredService<GrowthBookClient>()).Should().NotThrow().Subject;
        var second = provider.GetRequiredService<GrowthBookClient>();

        first.Should().BeSameAs(second, "a failed load must not leave the factory running again on every resolution");
        attempts.Should().Be(1);

        first.EvalFeature("anything", new UserContext { Attributes = new JObject { ["id"] = "user-1" } })
            .On.Should().BeFalse("without features every flag reads as its default");
    }

    /// <summary>
    /// Starting without features is worse than not starting for some applications, so failing fast
    /// stays available - but as something asked for rather than the default.
    /// </summary>
    [Fact]
    public void FailingFastIsAvailableAsAnOptIn()
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IDictionary<string, Feature>>(
                new FeatureLoadException("the features api is unreachable", 503)));

        var services = new ServiceCollection();

        services.AddGrowthBookClient(options =>
        {
            options.ClientKey = "sdk-test";
            options.LoggerFactory = NullLoggerFactory.Instance;
            options.FeatureRepository = repository;
            options.ThrowOnInitializationFailure = true;
        });

        using var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<GrowthBookClient>())
            .Should().Throw<FeatureLoadException>();
    }

    /// <summary>
    /// An application and a library it depends on can both register the client. AddSingleton would
    /// leave two descriptors, and two clients each polling the API.
    /// </summary>
    [Fact]
    public void RegisteringTwiceLeavesOneClient()
    {
        var services = new ServiceCollection();

        for (var i = 0; i < 2; i++)
        {
            services.AddGrowthBookClient(options =>
            {
                options.ClientKey = "sdk-test";
                options.LoggerFactory = NullLoggerFactory.Instance;
                options.FeatureRepository = CreateRepository();
            });
        }

        using var provider = services.BuildServiceProvider();

        provider.GetServices<GrowthBookClient>().Should().ContainSingle();
    }

    /// <summary>
    /// Registration must not write into the options the caller still holds, and the client must not
    /// be left sharing them.
    /// </summary>
    [Fact]
    public void TheCallersOptionsAreNotModifiedOrShared()
    {
        var options = new Options
        {
            ClientKey = "sdk-test",
            FeatureRepository = CreateRepository(),
            GlobalAttributes = new JObject { ["tier"] = "gold" }
        };

        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddGrowthBookClient(options);

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<GrowthBookClient>();

        options.LoggerFactory.Should().BeNull("registration resolved one for the client, not for the caller");

        client.SetGlobalAttributes(new JObject { ["tier"] = "platinum" });

        options.GlobalAttributes["tier"].Value<string>().Should().Be("gold",
            "the client was given a copy, so changing its state leaves the caller's options alone");
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
