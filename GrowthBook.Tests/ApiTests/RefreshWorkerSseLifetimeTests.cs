using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using GrowthBook.Api;
using GrowthBook.Api.SSE;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.ApiTests;

/// <summary>
/// The SSE client is reached from two threads: the caller's, through Cancel and Dispose, and the
/// refresh thread, through the mode check that creates and replaces it.
/// </summary>
public class RefreshWorkerSseLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static FeatureRefreshWorker NewWorker(ILogger<FeatureRefreshWorker> logger = null)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();

        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        return new FeatureRefreshWorker(
            logger ?? NullLogger<FeatureRefreshWorker>.Instance,
            httpClientFactory,
            new GrowthBookConfigurationOptions { ApiHost = "https://example.test", ClientKey = "key" },
            new InMemoryFeatureCache(cacheExpirationInSeconds: 60));
    }

    private static SSEClient AttachClient(FeatureRefreshWorker worker)
    {
        var client = new SSEClient(
            NullLogger<SSEClient>.Instance,
            Substitute.For<IHttpClientFactory>(),
            "https://example.test/sub/key");

        typeof(FeatureRefreshWorker).GetField("_sseClient", Private).SetValue(worker, client);

        return client;
    }

    private static object CurrentClient(FeatureRefreshWorker worker) =>
        typeof(FeatureRefreshWorker).GetField("_sseClient", Private).GetValue(worker);

    /// <summary>
    /// A refresh already in flight can reach the mode check after the worker has been disposed. If
    /// the reference is left behind, that check asks a disposed client for its connection status.
    /// </summary>
    [Fact]
    public void DisposingTheWorkerLetsGoOfTheSseClient()
    {
        var worker = NewWorker();

        AttachClient(worker);

        CurrentClient(worker).Should().NotBeNull("the test attached one to have something to release");

        worker.Dispose();

        CurrentClient(worker).Should().BeNull("a disposed client must not be left where it can still be asked questions");
    }

    /// <summary>
    /// Cancel is the caller's thread reaching the same field, so it has to take the same lock the
    /// refresh thread takes - and it must stay safe when there is no client at all.
    /// </summary>
    [Fact]
    public void CancellingWithoutAnSseClientIsSafe()
    {
        var worker = NewWorker();

        worker.Invoking(w => w.Cancel()).Should().NotThrow();
        worker.Invoking(w => w.Dispose()).Should().NotThrow();
    }

    /// <summary>
    /// A refresh in flight can reach the mode check after the worker is disposed. Clearing the client
    /// is not enough on its own: the check then finds nothing and starts a replacement that nobody
    /// will dispose, whose connection task reads a cancellation source that has already been disposed.
    /// </summary>
    [Fact]
    public void ADisposedWorkerDoesNotTryToStartAnotherSseClient()
    {
        var messages = new List<string>();
        var worker = NewWorker(new CapturingLogger(messages));

        typeof(FeatureRefreshWorker).GetField("_isServerSentEventsEnabled", Private).SetValue(worker, true);

        AttachClient(worker);

        worker.Dispose();

        typeof(FeatureRefreshWorker)
            .GetMethod("EnsureCorrectRefreshModeIsActive", Private)
            .Invoke(worker, null);

        CurrentClient(worker).Should().BeNull("a disposed worker has nothing left to connect with");

        // Without the guard the check finds no client and tries to build one, which fails on the
        // disposed cancellation source and is logged. Nothing is attempted at all now.
        messages.Should().NotContain(m => m.Contains("Error initializing SSE client"));
        messages.Should().NotContain(m => m.Contains("starting SSE client now"));
    }

    private sealed class CapturingLogger : ILogger<FeatureRefreshWorker>
    {
        private readonly List<string> _messages;

        public CapturingLogger(List<string> messages) => _messages = messages;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Written by the refresh thread from the API response and read when the refresh mode is decided.
    /// There is no deterministic test for a memory barrier, so this asserts the declaration itself.
    /// </summary>
    [Fact]
    public void TheServerSentEventsFlagIsVolatile()
    {
        var field = typeof(FeatureRefreshWorker).GetField("_isServerSentEventsEnabled", Private);

        field.Should().NotBeNull();
        field.GetRequiredCustomModifiers().Should().Contain(typeof(System.Runtime.CompilerServices.IsVolatile));
    }
}
