using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the Redis-backed sticky bucket store. The point of it is that a user keeps the same variation
/// across every process in a fleet, so the tests focus on the batch read, the write, and on a single bad
/// entry not taking the whole batch down.
/// </summary>
public class RedisStickyBucketServiceTests
{
    /// <summary>
    /// An in-memory stand-in for the application's Redis client, recording what was asked of it.
    /// </summary>
    private sealed class FakeRedis : IRedisCompatibleClient
    {
        // Locked on every access because the SDK dispatches writes for an async store without awaiting
        // them, so a test thread and a background one touch this at the same time.
        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _store = new Dictionary<string, string>();
        private readonly SemaphoreSlim _writes = new SemaphoreSlim(0);

        public List<string[]> MultiGetCalls { get; } = new List<string[]>();
        public int SetCallCount { get; private set; }
        public Exception ThrowOnMultiGet { get; set; }
        public Exception ThrowOnSet { get; set; }

        public void Put(string key, string value)
        {
            lock (_lock)
            {
                _store[key] = value;
            }
        }

        public string Get(string key)
        {
            lock (_lock)
            {
                return _store.TryGetValue(key, out var value) ? value : null;
            }
        }

        public Task<string[]> MultiGetAsync(string[] keys, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                MultiGetCalls.Add(keys);
            }

            if (ThrowOnMultiGet != null)
            {
                throw ThrowOnMultiGet;
            }

            return Task.FromResult(keys.Select(Get).ToArray());
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                SetCallCount++;
            }

            if (ThrowOnSet != null)
            {
                throw ThrowOnSet;
            }

            Put(key, value);
            _writes.Release();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Waits for a write to land, so a test never has to assert against work the SDK dispatched
        /// without awaiting. Returns false if none arrived in time.
        /// </summary>
        public Task<bool> WaitForWriteAsync() => _writes.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string DocumentJson(string name, string value, params (string Key, string Variation)[] assignments) =>
        JsonConvert.SerializeObject(new StickyAssignmentsDocument(name, value,
            assignments.ToDictionary(x => x.Key, x => x.Variation)));

    [Fact]
    public async Task AllAttributesAreReadInOneRoundTrip()
    {
        var redis = new FakeRedis();
        redis.Put("id||user-1", DocumentJson("id", "user-1", ("exp__0", "1")));
        redis.Put("company||acme", DocumentJson("company", "acme", ("exp__0", "0")));

        var service = new RedisStickyBucketService(redis);

        var documents = await service.GetAllAssignmentsAsync(new[] { "id||user-1", "company||acme" });

        documents.Should().HaveCount(2);
        documents["id||user-1"].Assignments["exp__0"].Should().Be("1");
        documents["company||acme"].Assignments["exp__0"].Should().Be("0");

        redis.MultiGetCalls.Should().HaveCount(1, "because reading one key at a time would multiply the round trips by the number of attributes");
        redis.MultiGetCalls[0].Should().HaveCount(2);
    }

    [Fact]
    public async Task MissingKeysAreSkippedRatherThanFailing()
    {
        var redis = new FakeRedis();
        redis.Put("id||user-1", DocumentJson("id", "user-1", ("exp__0", "1")));

        var service = new RedisStickyBucketService(redis);

        var documents = await service.GetAllAssignmentsAsync(new[] { "id||user-1", "id||nobody" });

        documents.Should().HaveCount(1);
        documents.Should().ContainKey("id||user-1");
    }

    [Fact]
    public async Task OneUnreadableEntryDoesNotDiscardTheRest()
    {
        var redis = new FakeRedis();
        redis.Put("id||user-1", "{ this is not json");
        redis.Put("id||user-2", DocumentJson("id", "user-2", ("exp__0", "1")));

        var service = new RedisStickyBucketService(redis);

        var documents = await service.GetAllAssignmentsAsync(new[] { "id||user-1", "id||user-2" });

        documents.Should().HaveCount(1, "because a hand-edited or truncated entry must not cost us the other users' assignments");
        documents.Should().ContainKey("id||user-2");
    }

    [Fact]
    public async Task AnEntryMissingRequiredFieldsIsRejected()
    {
        var redis = new FakeRedis();
        redis.Put("id||user-1", "{\"assignments\":{\"exp__0\":\"1\"}}");

        var service = new RedisStickyBucketService(redis);

        var documents = await service.GetAllAssignmentsAsync(new[] { "id||user-1" });

        documents.Should().BeEmpty("because without an attribute name and value the document cannot be keyed");
    }

    [Fact]
    public async Task SavingWritesTheDocumentUnderItsFormattedAttribute()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        await service.SaveAssignmentsAsync(new StickyAssignmentsDocument("id", "user-1",
            new Dictionary<string, string> { ["exp__0"] = "1" }));

        var stored = redis.Get("id||user-1");

        stored.Should().NotBeNull("because the document has to land under its formatted attribute");
    }

    [Fact]
    public async Task DocumentsAreWrittenInTheFieldNamesTheOtherSdksUse()
    {
        // The reference SDKs store { attributeName, attributeValue, assignments }. Writing the CLR property
        // names instead would let this SDK read their documents but not the other way round - so a mixed
        // fleet would silently stop sharing assignments, which is the exact failure this store exists to
        // prevent.
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        await service.SaveAssignmentsAsync(new StickyAssignmentsDocument("id", "user-1",
            new Dictionary<string, string> { ["exp__0"] = "1" }));

        var stored = JObject.Parse(redis.Get("id||user-1"));

        stored.Properties().Select(x => x.Name).Should().BeEquivalentTo(new[] { "attributeName", "attributeValue", "assignments" });
        stored["attributeName"].Value<string>().Should().Be("id");
        stored["attributeValue"].Value<string>().Should().Be("user-1");
        stored["assignments"]["exp__0"].Value<string>().Should().Be("1");
    }

    [Fact]
    public async Task ADocumentWrittenByAnotherSdkIsReadable()
    {
        var redis = new FakeRedis();
        redis.Put("id||user-1", "{\"attributeName\":\"id\",\"attributeValue\":\"user-1\",\"assignments\":{\"exp__0\":\"1\"}}");

        var service = new RedisStickyBucketService(redis);

        var document = await service.GetAssignmentsAsync("id", "user-1");

        document.Should().NotBeNull("because the store is shared with SDKs that write camelCase");
        document.Assignments["exp__0"].Should().Be("1");
    }

    [Fact]
    public async Task ASavedDocumentReadsBackIdentically()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        var original = new StickyAssignmentsDocument("id", "user-1",
            new Dictionary<string, string> { ["exp__0"] = "1", ["other__2"] = "0" });

        await service.SaveAssignmentsAsync(original);

        var roundTripped = await service.GetAssignmentsAsync("id", "user-1");

        roundTripped.Should().NotBeNull();
        roundTripped.AttributeName.Should().Be("id");
        roundTripped.AttributeValue.Should().Be("user-1");
        roundTripped.Assignments.Should().BeEquivalentTo(original.Assignments);
    }

    [Fact]
    public async Task TheKeyPrefixIsAppliedToReadsAndWrites()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis, keyPrefix: "gb:staging:");

        await service.SaveAssignmentsAsync(new StickyAssignmentsDocument("id", "user-1",
            new Dictionary<string, string> { ["exp__0"] = "1" }));

        redis.Get("gb:staging:id||user-1").Should().NotBeNull("because the prefix lets one Redis serve several environments");
        redis.Get("id||user-1").Should().BeNull();

        var read = await service.GetAssignmentsAsync("id", "user-1");

        read.Should().NotBeNull("because reads have to use the same prefix as writes");
    }

    [Fact]
    public async Task ARedisFailureOnReadDegradesToNoAssignments()
    {
        var redis = new FakeRedis { ThrowOnMultiGet = new TimeoutException("redis is down") };
        var service = new RedisStickyBucketService(redis);

        Func<Task> read = () => service.GetAllAssignmentsAsync(new[] { "id||user-1" });

        await read.Should().NotThrowAsync("because an unreachable store must degrade to unsticky bucketing rather than break evaluation");
        (await service.GetAllAssignmentsAsync(new[] { "id||user-1" })).Should().BeEmpty();
    }

    [Fact]
    public async Task ARedisFailureOnWriteDoesNotPropagate()
    {
        var redis = new FakeRedis { ThrowOnSet = new TimeoutException("redis is down") };
        var service = new RedisStickyBucketService(redis);

        Func<Task> save = () => service.SaveAssignmentsAsync(new StickyAssignmentsDocument("id", "user-1"));

        await save.Should().NotThrowAsync("because assignments are written behind evaluation and a failed write must not surface to the caller");
    }

    [Fact]
    public async Task NoAttributesMeansNoRoundTrip()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        var documents = await service.GetAllAssignmentsAsync(new string[0]);

        documents.Should().BeEmpty();
        redis.MultiGetCalls.Should().BeEmpty("because there is nothing to ask for");
    }

    [Fact]
    public async Task DuplicateAndBlankAttributesAreNotSentToRedis()
    {
        var redis = new FakeRedis();
        var service = new RedisStickyBucketService(redis);

        await service.GetAllAssignmentsAsync(new[] { "id||user-1", "id||user-1", "", null, "  " });

        redis.MultiGetCalls.Should().HaveCount(1);
        redis.MultiGetCalls[0].Should().BeEquivalentTo(new[] { "id||user-1" });
    }

    [Fact]
    public void TheClientIsRequired()
    {
        Action construct = () => new RedisStickyBucketService(null);

        construct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ItPlugsIntoAGrowthBookInstanceAsTheAsyncStickyBucketService()
    {
        const string FeatureKey = "sticky-feature";

        var redis = new FakeRedis();
        redis.Put("id||user-1", DocumentJson("id", "user-1", ("my-experiment__0", "1")));

        var feature = new Feature
        {
            DefaultValue = false,
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Key = "my-experiment",
                    Variations = new JArray(false, true),
                    Coverage = 1d,
                    Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
                }
            }
        };

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureKey] = feature },
            AsyncStickyBucketService = new RedisStickyBucketService(redis)
        });

        await growthBook.LoadStickyBucketAssignmentsAsync();

        var result = growthBook.EvalFeature(FeatureKey);

        result.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the assignment came from the shared store");
        result.On.Should().BeTrue("because the stored assignment points at variation index 1");
    }

    private static Feature FleetFeature() => new Feature
    {
        DefaultValue = false,
        Rules = new List<FeatureRule>
        {
            new FeatureRule
            {
                Key = "my-experiment",
                Variations = new JArray(false, true),
                Coverage = 1d,
                Meta = new List<VariationMeta> { new VariationMeta { Key = "0" }, new VariationMeta { Key = "1" } }
            }
        }
    };

    private static GrowthBook NewFleetInstance(IAsyncStickyBucketService service) => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature> { ["sticky-feature"] = FleetFeature() },
        AsyncStickyBucketService = service
    });

    [Fact]
    public async Task AnAssignmentMadeOnOneInstanceReachesTheSharedStore()
    {
        var redis = new FakeRedis();

        using var first = NewFleetInstance(new RedisStickyBucketService(redis));

        await first.LoadStickyBucketAssignmentsAsync();

        var result = first.EvalFeature("sticky-feature");

        result.ExperimentResult.StickyBucketUsed.Should().BeFalse("because nothing was stored yet - this assignment came from hashing");

        var written = await redis.WaitForWriteAsync();

        written.Should().BeTrue("because evaluating an experiment has to persist the assignment for the rest of the fleet");

        var stored = redis.Get("id||user-1");

        stored.Should().NotBeNull("because the document belongs under its formatted attribute");
        JObject.Parse(stored)["assignments"]["my-experiment__0"].Value<string>()
            .Should().Be(result.ExperimentResult.Key, "because the variation this instance picked is what the others must reuse");
    }

    [Fact]
    public async Task ASecondInstanceReusesTheVariationTheFirstOneChose()
    {
        var redis = new FakeRedis();

        using var first = NewFleetInstance(new RedisStickyBucketService(redis));

        await first.LoadStickyBucketAssignmentsAsync();

        var firstResult = first.EvalFeature("sticky-feature");

        (await redis.WaitForWriteAsync()).Should().BeTrue();

        // A different instance over the same Redis - equally, the same instance after a restart. It gets a
        // fresh service and no in-process assignments of its own.
        using var second = NewFleetInstance(new RedisStickyBucketService(redis));

        await second.LoadStickyBucketAssignmentsAsync();

        var secondResult = second.EvalFeature("sticky-feature");

        secondResult.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the second instance read the assignment the first one stored");
        secondResult.ExperimentResult.Key.Should().Be(firstResult.ExperimentResult.Key);
        secondResult.On.Should().Be(firstResult.On, "because a user must not flip variation by landing on another instance");
    }
}
