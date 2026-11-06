using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Api;
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
    /// The API can answer 200 with no features at all. That is a failed load like any other, so failing
    /// fast has to apply to it too, not only to a load that throws.
    /// </summary>
    [Fact]
    public void ANoFeaturesResponseFailsStartupWhenAskedTo()
    {
        var services = new ServiceCollection();

        services.AddGrowthBookClient(NoFeaturesOptions(throwOnFailure: true, NullLoggerFactory.Instance));

        using var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<GrowthBookClient>())
            .Should().Throw<FeatureLoadException>();
    }

    /// <summary>
    /// Without failing fast the client still starts, but a load that brought nothing back is logged as the
    /// failure it is rather than passing in silence.
    /// </summary>
    [Fact]
    public void ANoFeaturesResponseIsLoggedWhenStartupGoesOn()
    {
        var logged = new List<(LogLevel Level, string Message)>();
        var services = new ServiceCollection();

        services.AddGrowthBookClient(NoFeaturesOptions(throwOnFailure: false, new CapturingLoggerFactory(logged)));

        using var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredService<GrowthBookClient>()).Should().NotThrow();

        lock (logged)
        {
            logged.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Message.Contains("could not load its features"));
        }
    }

    private static Options NoFeaturesOptions(bool throwOnFailure, ILoggerFactory loggerFactory) => new Options
    {
        ClientKey = "sdk-test",
        ApiHost = "https://growthbook.test",
        LoggerFactory = loggerFactory,
        ThrowOnInitializationFailure = throwOnFailure,
        HttpClientFactory = new StaticResponseHttpClientFactory("{}")
    };

    private sealed class StaticResponseHttpClientFactory : HttpClientFactory
    {
        private readonly string _body;

        public StaticResponseHttpClientFactory(string body) => _body = body;

        protected internal override System.Net.Http.HttpClient CreateClient(Func<System.Net.Http.HttpClient, System.Net.Http.HttpClient> configure) =>
            configure(new System.Net.Http.HttpClient(new StaticResponseHandler(_body)));
    }

    private sealed class StaticResponseHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly string _body;

        public StaticResponseHandler(string body) => _body = body;

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _logged;

        public CapturingLoggerFactory(List<(LogLevel Level, string Message)> logged) => _logged = logged;

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_logged)
            {
                _logged.Add((logLevel, formatter(state, exception)));
            }
        }
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

    /// <summary>
    /// Two containers built from one collection each build a client. A client changes its options in
    /// place, so the two must not hold the same instance.
    /// </summary>
    [Fact]
    public void ClientsFromSeparateContainersDoNotShareOptions()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddGrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            FeatureRepository = RepositoryWith(new Feature
            {
                DefaultValue = JToken.FromObject(false),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule { Condition = JObject.Parse("{\"tier\":\"gold\"}"), Force = JToken.FromObject(true) }
                }
            }),
            GlobalAttributes = new JObject { ["tier"] = "gold" }
        });

        using var firstProvider = services.BuildServiceProvider();
        using var secondProvider = services.BuildServiceProvider();

        var first = firstProvider.GetRequiredService<GrowthBookClient>();
        var second = secondProvider.GetRequiredService<GrowthBookClient>();

        first.SetGlobalAttributes(new JObject { ["tier"] = "platinum" });

        second.IsOn("flag", new UserContext { Attributes = new JObject { ["id"] = "user-1" } }).Should().BeTrue(
            "the second client still targets the gold tier it was registered with");
    }

    [Fact]
    public void SavedGroupsSurviveRegistrationFromPreparedOptions()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddGrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            FeatureRepository = RepositoryWith(new Feature
            {
                DefaultValue = JToken.FromObject(false),
                Rules = new List<FeatureRule>
                {
                    new FeatureRule { Condition = JObject.Parse("{\"id\":{\"$inGroup\":\"beta\"}}"), Force = JToken.FromObject(true) }
                }
            }),
            SavedGroups = new JObject { ["beta"] = new JArray("user-1") }
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<GrowthBookClient>()
            .IsOn("flag", new UserContext { Attributes = new JObject { ["id"] = "user-1" } })
            .Should().BeTrue("the saved groups were part of the options handed to registration");
    }

    [Fact]
    public void CloningKeepsTheHttpClientFactory()
    {
        var factory = new HttpClientFactory();

        new Options { HttpClientFactory = factory }.Clone().HttpClientFactory.Should().BeSameAs(factory,
            "a property Clone leaves out is lost to every client registered from prepared options");
    }

    private static IGrowthBookFeatureRepository RepositoryWith(Feature flag)
    {
        var repository = Substitute.For<IGrowthBookFeatureRepository>();

        repository
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IDictionary<string, Feature>>(new Dictionary<string, Feature> { ["flag"] = flag }));

        return repository;
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
