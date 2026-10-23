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

    private static FeatureRefreshWorker NewWorker()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();

        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        return new FeatureRefreshWorker(
            NullLogger<FeatureRefreshWorker>.Instance,
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
