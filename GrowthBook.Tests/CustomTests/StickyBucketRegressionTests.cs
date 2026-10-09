using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.MultiUser;
using GrowthBook.MultiUser.Configuration;
using GrowthBook.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class StickyBucketRegressionTests
{
    private const string FeatureId = "my-feature";
    private const string RuleKey = "sticky-exp";

    private static FeatureRule CreateRule(string fallbackAttribute = null, double coverage = 1.0) =>
        new FeatureRule
        {
            Key = RuleKey,
            Variations = JArray.FromObject(new[] { "control", "treatment" }),
            HashAttribute = "id",
            FallbackAttribute = fallbackAttribute,
            Coverage = coverage,
            Weights = new List<double> { 0.5, 0.5 },
            Meta = new List<VariationMeta>
            {
                new VariationMeta { Key = "0" },
                new VariationMeta { Key = "1" }
            }
        };

    private static IDictionary<string, Feature> CreateFeatures(FeatureRule rule) =>
        new Dictionary<string, Feature>
        {
            [FeatureId] = new Feature { Rules = new List<FeatureRule> { rule } }
        };

    // Bug: after the first EvalFeature assigned a user, _stickyBucketAssignmentDocs was not
    // updated with the new document. A subsequent eval on the same GrowthBook instance would
    // not find the sticky assignment and could produce a different result when experiment
    // conditions changed.
    [Fact]
    public void EvalFeature_SecondEval_UsesStickyAssignmentCreatedByFirstEval()
    {
        var rule = CreateRule();
        var gb = new GrowthBook(new Context
        {
            Features = CreateFeatures(rule),
            Attributes = JObject.FromObject(new { id = "user-sticky-1" }),
            StickyBucketService = new InMemoryStickyBucketService()
        });

        var first = gb.EvalFeature(FeatureId);

        first.ExperimentResult.InExperiment.Should().BeTrue();
        first.ExperimentResult.StickyBucketUsed.Should().BeFalse("no pre-existing doc on first eval");

        // Drop coverage to 0 — without a sticky assignment the user would leave the experiment.
        rule.Coverage = 0;

        var second = gb.EvalFeature(FeatureId);

        second.ExperimentResult.InExperiment.Should().BeTrue(
            "sticky assignment created by first eval should keep user enrolled");
        second.ExperimentResult.StickyBucketUsed.Should().BeTrue();
        second.ExperimentResult.VariationId.Should().Be(first.ExperimentResult.VariationId);
    }

    // Bug: when UpdateAttributes was called, RefreshStickyBuckets was not invoked, so the
    // new user's sticky documents were never loaded from the service.
    [Fact]
    public void UpdateAttributes_RefreshesStickyBucketDocs_ForNewUser()
    {
        var service = new InMemoryStickyBucketService();

        // Pre-save user-a's assignment to variation 1.
        // Key format: "{ruleKey}__{bucketVersion}" — BucketVersion defaults to 0.
        service.SaveAssignments(new StickyAssignmentsDocument("id", "user-a",
            new Dictionary<string, string> { [$"{RuleKey}__0"] = "1" }));

        var gb = new GrowthBook(new Context
        {
            Features = CreateFeatures(CreateRule()),
            Attributes = JObject.FromObject(new { id = "user-b" }),
            StickyBucketService = service
        });

        gb.UpdateAttributes(new Dictionary<string, object> { ["id"] = "user-a" });

        var result = gb.EvalFeature(FeatureId);

        result.ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "user-a's sticky doc loaded from service after UpdateAttributes");
        result.ExperimentResult.VariationId.Should().Be(1);
    }

    // Bug: when a user's fallback attribute value was an empty string (not null), the code
    // would look up "fallbackAttr||" in stickyAssignmentDocs, potentially finding a phantom
    // document that happened to have that key.
    [Fact]
    public void EvalFeature_EmptyFallbackAttributeValue_DoesNotUseFallbackDoc()
    {
        var service = new InMemoryStickyBucketService();

        // Phantom document keyed with empty deviceId — must not be used when deviceId = "".
        var phantomDoc = new StickyAssignmentsDocument("deviceId", "",
            new Dictionary<string, string> { [$"{RuleKey}__0"] = "1" });
        service.SaveAssignments(phantomDoc);

        var gb = new GrowthBook(new Context
        {
            Features = CreateFeatures(CreateRule(fallbackAttribute: "deviceId")),
            Attributes = JObject.FromObject(new { id = "user-1", deviceId = "" }),
            StickyBucketService = service,
            StickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument>
            {
                [phantomDoc.FormattedAttribute] = phantomDoc
            }
        });

        var result = gb.EvalFeature(FeatureId);

        result.ExperimentResult.StickyBucketUsed.Should().BeFalse(
            "empty deviceId should not be used as a fallback lookup key");
    }

    // GrowthBookClient: when StickyBucketService is provided in Options but not in UserContext,
    // CreateEvaluator should auto-load the user's sticky docs from the service.
    [Fact]
    public async Task GrowthBookClient_EvalFeature_AutoLoadsStickyDocsFromOptions()
    {
        const string ClientFeatureId = "client-feature";
        const string ClientRuleKey = "client-sticky-exp";

        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument("id", "user-1",
            new Dictionary<string, string> { [$"{ClientRuleKey}__0"] = "1" }));

        var features = new Dictionary<string, Feature>
        {
            [ClientFeatureId] = new Feature
            {
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Key = ClientRuleKey,
                        Variations = JArray.FromObject(new[] { "control", "treatment" }),
                        HashAttribute = "id",
                        Coverage = 1.0,
                        Weights = new List<double> { 0.5, 0.5 },
                        Meta = new List<VariationMeta>
                        {
                            new VariationMeta { Key = "0" },
                            new VariationMeta { Key = "1" }
                        }
                    }
                }
            }
        };

        var mockRepo = Substitute.For<IGrowthBookFeatureRepository>();
        mockRepo
            .GetFeatures(Arg.Any<GrowthBookRetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IDictionary<string, Feature>>(features));

        using var client = new GrowthBookClient(new Options
        {
            ClientKey = "sdk-test",
            LoggerFactory = NullLoggerFactory.Instance,
            FeatureRepository = mockRepo,
            StickyBucketService = service
        });

        await client.InitializeAsync();

        var result = client.EvalFeature(ClientFeatureId, new UserContext
        {
            Attributes = JObject.Parse("{\"id\": \"user-1\"}")
            // No StickyBucketAssignmentDocs — should be auto-loaded from Options.StickyBucketService
        });

        result.ExperimentResult.StickyBucketUsed.Should().BeTrue(
            "sticky docs should be auto-loaded from Options.StickyBucketService");
        result.ExperimentResult.VariationId.Should().Be(1,
            "user-1 has a pre-saved sticky assignment for variation 1");
    }
}
