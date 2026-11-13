using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Evaluation;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// <see cref="RedisStickyBucketService"/> keeps documents in the format <c>RedisStickyBucketService</c> uses in the
/// JavaScript SDK, so services written in either SDK can share one Redis.
/// </summary>
public class RedisStickyBucketServiceTests
{
    private static StickyAssignmentsDocument Document(string userId, string variation) =>
        new StickyAssignmentsDocument("id", userId, new Dictionary<string, string> { ["exp__0"] = variation });

    [Fact]
    public void ADocumentIsStoredInTheJavaScriptFormatAndReadBack()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        service.SaveAssignments(Document("user-1", "1"));

        redis.Values.Should().ContainKey("id||user-1")
            .WhoseValue.Should().Be(@"{""attributeName"":""id"",""attributeValue"":""user-1"",""assignments"":{""exp__0"":""1""}}");
        service.GetAssignments("id", "user-1").Assignments.Should().Equal(new Dictionary<string, string> { ["exp__0"] = "1" });
    }

    [Fact]
    public void ADocumentWrittenByTheJavaScriptSdkIsRead()
    {
        var redis = new FakeRedis();
        redis.Values["gb_id||user-1"] = @"{""attributeName"":""id"",""attributeValue"":""user-1"",""assignments"":{""exp__0"":""1"",""other__2"":""control""}}";

        var document = new RedisStickyBucketService(redis, "gb_").GetAssignments("id", "user-1");

        document.FormattedAttribute.Should().Be("id||user-1");
        document.Assignments.Should().HaveCount(2).And.Contain("other__2", "control");
    }

    [Fact]
    public void AllIdentifiersAreReadWithOneMultiGetAndMissingOnesLeftOut()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);
        service.SaveAssignments(Document("user-1", "0"));
        service.SaveAssignments(new StickyAssignmentsDocument("deviceId", "device-9", new Dictionary<string, string> { ["exp__0"] = "1" }));

        var documents = service.GetAllAssignments(new[] { "id||user-1", "deviceId||device-9", "id||nobody" });

        redis.MultiGetCalls.Should().Be(1);
        documents.Keys.Should().BeEquivalentTo("id||user-1", "deviceId||device-9");
    }

    [Fact]
    public void ThePrefixIsAppliedToEveryKey()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis, "gb_");

        service.SaveAssignments(Document("user-1", "0"));

        redis.Values.Keys.Should().Equal("gb_id||user-1");
        service.GetAssignments("id", "user-1").Should().NotBeNull();
    }

    /// <summary>
    /// A value the service cannot use is skipped rather than failing the read, and so is a document naming another
    /// identifier than its key: published, it would hand one user another user's variation.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData(@"{""attributeName"":""id"",""attributeValue"":""user-1""}")]
    [InlineData(@"{""attributeName"":""id"",""assignments"":{}}")]
    [InlineData(@"{""attributeName"":""id"",""attributeValue"":""someone-else"",""assignments"":{""exp__0"":""1""}}")]
    public void AValueThatIsNotThisUsersDocumentIsSkipped(string stored)
    {
        var redis = new FakeRedis();
        redis.Values["id||user-1"] = stored;

        new RedisStickyBucketService(redis).GetAllAssignments(new[] { "id||user-1" }).Should().BeEmpty();
    }

    [Fact]
    public void AValueThatLooksLikeADateStaysTheTextItWas()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);
        var document = new StickyAssignmentsDocument("signupDate", "2024-01-01T00:00:00Z", new Dictionary<string, string> { ["exp__0"] = "2024-02-02T00:00:00Z" });

        service.SaveAssignments(document);

        var read = service.GetAssignments("signupDate", "2024-01-01T00:00:00Z");
        read.Should().NotBeNull();
        read.Assignments["exp__0"].Should().Be("2024-02-02T00:00:00Z");
    }

    /// <summary>
    /// An unreachable store throws rather than looking empty, so a caller does not replace what it holds with nothing.
    /// </summary>
    [Fact]
    public void AClientThatFailsThrows()
    {
        var redis = Substitute.For<IRedisCompatibleClient>();
        redis.MultiGet(Arg.Any<string[]>()).Returns(_ => throw new TimeoutException("redis unavailable"));

        var service = new RedisStickyBucketService(redis);

        service.Invoking(s => s.GetAllAssignments(new[] { "id||user-1" })).Should().Throw<TimeoutException>();
    }

    [Fact]
    public async Task AReturningUserKeepsTheirVariationThroughRedisAfterTheWeightsChange()
    {
        var redis = new FakeRedis();

        async Task<int> ValueFor(double first, double second)
        {
            var repository = Substitute.For<IGrowthBookFeatureRepository>();
            repository.GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IDictionary<string, Feature>>(Features(first, second)));

            using var client = new GrowthBookClient(new Options
            {
                ClientKey = "sdk-test",
                LoggerFactory = NullLoggerFactory.Instance,
                FeatureRepository = repository,
                StickyBucketService = new RedisStickyBucketService(redis)
            });

            await client.InitializeAsync();

            return client.GetFeature("feature", -1, new UserContext { Attributes = new JObject { ["id"] = "user-1" } });
        }

        (await ValueFor(1, 0)).Should().Be(0);
        (await ValueFor(0, 1)).Should().Be(0, "the first visit's variation was stored in Redis");
    }

    private static IDictionary<string, Feature> Features(double first, double second) => new Dictionary<string, Feature>
    {
        ["feature"] = new Feature
        {
            DefaultValue = JToken.FromObject(-1),
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Key = "exp",
                    Variations = new JArray(0, 1),
                    Weights = new List<double> { first, second },
                    Coverage = 1,
                    HashAttribute = "id",
                    Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
                }
            }
        }
    };

    private sealed class FakeRedis : IRedisCompatibleClient
    {
        private int _multiGetCalls;

        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

        public int MultiGetCalls => Volatile.Read(ref _multiGetCalls);

        public string[] MultiGet(string[] keys)
        {
            Interlocked.Increment(ref _multiGetCalls);

            lock (Values) return keys.Select(key => Values.TryGetValue(key, out var value) ? value : null).ToArray();
        }

        public void Set(string key, string value)
        {
            lock (Values) Values[key] = value;
        }
    }
}
