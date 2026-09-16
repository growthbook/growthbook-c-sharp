using System.Collections.Generic;
using FluentAssertions;
using GrowthBook.Services;
using GrowthBook.Utilities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class StickyBucketAutoRefreshTests : UnitTest
{
    /// <summary>
    /// Like <see cref="InMemoryStickyBucketService"/> but assignments can also be removed, so a test can
    /// model a store that no longer holds a document it used to.
    /// </summary>
    private sealed class DeletableStickyBucketService : IStickyBucketService
    {
        private readonly Dictionary<string, StickyAssignmentsDocument> _documents = new Dictionary<string, StickyAssignmentsDocument>();

        public void Delete(string formattedAttribute) => _documents.Remove(formattedAttribute);

        public StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue)
        {
            var key = new StickyAssignmentsDocument(attributeName, attributeValue).FormattedAttribute;

            return _documents.TryGetValue(key, out var document) ? document : null;
        }

        public void SaveAssignments(StickyAssignmentsDocument document) => _documents[document.FormattedAttribute] = document;

        public IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes)
        {
            var found = new Dictionary<string, StickyAssignmentsDocument>();

            foreach (var attribute in attributes)
            {
                if (_documents.TryGetValue(attribute, out var document))
                {
                    found[attribute] = document;
                }
            }

            return found;
        }
    }

    private static Feature CreateExperimentFeature()
    {
        return new Feature
        {
            DefaultValue = false,
            Rules = new List<FeatureRule>
            {
                new FeatureRule
                {
                    Variations = new JArray(false, true),
                    Coverage = 1d,
                    Meta = new List<VariationMeta>
                    {
                        new VariationMeta { Key = "0" },
                        new VariationMeta { Key = "1" }
                    }
                }
            }
        };
    }

    [Fact]
    public void ConstructorAutomaticallyPullsStickyBucketAssignmentsFromTheService()
    {
        const string FeatureName = "test-feature";

        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument(
            "id",
            "user-1",
            new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service
            // Deliberately not setting StickyBucketAssignmentDocs - the SDK must pull it from the service itself.
        };

        var growthBook = new GrowthBook(context);
        var result = growthBook.EvalFeature(FeatureName);

        result.ExperimentResult.Should().NotBeNull();
        result.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the assignment doc was already in the service before construction");
        result.On.Should().BeTrue("because the pre-saved assignment points at variation index 1 (true)");
    }

    [Fact]
    public void UpdateAttributesRefreshesStickyBucketAssignmentsForTheNewIdentifier()
    {
        const string FeatureName = "test-feature";

        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument(
            "id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));
        service.SaveAssignments(new StickyAssignmentsDocument(
            "id", "user-2", new Dictionary<string, string> { [$"{FeatureName}__0"] = "0" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service
        };

        var growthBook = new GrowthBook(context);
        growthBook.EvalFeature(FeatureName).On.Should().BeTrue("because user-1 is stuck on variation 1");

        growthBook.UpdateAttributes(new { id = "user-2" });

        var result = growthBook.EvalFeature(FeatureName);
        result.ExperimentResult.StickyBucketUsed.Should().BeTrue("because UpdateAttributes must trigger a refresh for the new identifier, not just keep serving user-1's cached doc");
        result.On.Should().BeFalse("because user-2 is stuck on variation 0");
    }

    [Fact]
    public void NewlySavedAssignmentIsVisibleToLaterEvaluationsOnTheSameInstance()
    {
        const string FeatureName = "test-feature";

        var service = new InMemoryStickyBucketService();

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        // First evaluation has nothing sticky to read, so it buckets by hashing and persists the result.
        var first = growthBook.EvalFeature(FeatureName);
        first.ExperimentResult.StickyBucketUsed.Should().BeFalse("because there was no prior assignment to reuse");

        // The second evaluation must honor the assignment the first one just wrote. This only works if
        // the in-memory docs were updated at save time, not just the service.
        var second = growthBook.EvalFeature(FeatureName);
        second.ExperimentResult.StickyBucketUsed.Should().BeTrue("because the assignment saved during the first evaluation must be visible in-memory");
        second.ExperimentResult.VariationId.Should().Be(first.ExperimentResult.VariationId);
    }

    [Fact]
    public void RefreshDropsAnAssignmentThatWasDeletedFromTheService()
    {
        const string FeatureName = "test-feature";

        var service = new DeletableStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument(
            "id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service
        };

        var growthBook = new GrowthBook(context);

        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed
            .Should().BeTrue("because the stored assignment is in effect to begin with");

        // Somebody clears the assignment out of the store - a bucket-version reset, a GDPR erase, an admin
        // action. A refresh has to stop honoring it rather than keep serving the stale in-memory copy.
        service.Delete("id||user-1");
        growthBook.UpdateAttributes(new { id = "user-1" });

        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed
            .Should().BeFalse("because a refresh replaces the in-memory docs outright instead of merging over them");
    }

    [Fact]
    public void AssignmentDocsSuppliedOnTheContextSurviveTheFirstRefresh()
    {
        const string FeatureName = "test-feature";

        // The caller only sets StickyBucketAssignmentDocs and never touches the service directly, which is
        // all the public API asks of them. Construction has to seed the store so the first refresh - which
        // replaces the in-memory docs with whatever the store holds - doesn't throw the seed data away.
        var service = new DeletableStickyBucketService();

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service,
            StickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument>
            {
                ["id||user-1"] = new StickyAssignmentsDocument(
                    "id", "user-1", new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" })
            }
        };

        var growthBook = new GrowthBook(context);

        var result = growthBook.EvalFeature(FeatureName);
        result.ExperimentResult.StickyBucketUsed.Should().BeTrue("because docs handed in on the Context must not be lost by the first refresh");
        result.On.Should().BeTrue("because the supplied assignment points at variation index 1");
    }

    [Fact]
    public void RefreshIsANoOpWhenNoStickyBucketServiceIsConfigured()
    {
        const string FeatureName = "test-feature";

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() }
        };

        // Should not throw despite there being no IStickyBucketService to pull assignments from.
        var growthBook = new GrowthBook(context);
        growthBook.UpdateAttributes(new { id = "user-2" });

        var result = growthBook.EvalFeature(FeatureName);
        result.ExperimentResult.StickyBucketUsed.Should().BeFalse();
    }

    [Fact]
    public void DeriveStickyBucketIdentifierAttributesOnlyConsidersExperimentRulesNotForceRules()
    {
        var features = new Dictionary<string, Feature>
        {
            ["force-only"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule> { new FeatureRule { Force = true, HashAttribute = "should-not-appear" } }
            },
            ["experiment-with-fallback"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule>
                {
                    new FeatureRule
                    {
                        Variations = new JArray(false, true),
                        HashAttribute = "deviceId",
                        FallbackAttribute = "cookieId"
                    }
                }
            }
        };

        var attributes = ExperimentUtilities.DeriveStickyBucketIdentifierAttributes(features, new List<Experiment>());

        attributes.Should().Contain("deviceId").And.Contain("cookieId");
        attributes.Should().NotContain("should-not-appear", "because force rules never run an experiment and so can't need sticky bucketing");
    }

    [Fact]
    public void DeriveStickyBucketIdentifierAttributesDefaultsToIdAndIncludesStandaloneExperiments()
    {
        var features = new Dictionary<string, Feature>
        {
            ["no-explicit-hash-attribute"] = new Feature
            {
                DefaultValue = false,
                Rules = new List<FeatureRule> { new FeatureRule { Variations = new JArray(false, true) } }
            }
        };
        var experiments = new List<Experiment>
        {
            new Experiment { Key = "standalone-exp", HashAttribute = "orgId" }
        };

        var attributes = ExperimentUtilities.DeriveStickyBucketIdentifierAttributes(features, experiments);

        attributes.Should().Contain("id", "because HashAttribute defaults to \"id\" when unset");
        attributes.Should().Contain("orgId", "because standalone Experiments contribute their hash attribute too");
    }

    [Fact]
    public void ConstructingWithNullAttributesDoesNotThrow()
    {
        const string FeatureName = "test-feature";

        var context = new Context
        {
            // Explicitly null rather than left at the default empty object. The constructor assigns this
            // straight to the backing field, so the refresh it then runs has no attributes to resolve against.
            Attributes = null,
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = new InMemoryStickyBucketService()
        };

        GrowthBook growthBook = null;

        var construct = () => growthBook = new GrowthBook(context);

        construct.Should().NotThrow("because an absent identifier means there is nothing to ask the store for, not a crash");
        growthBook.EvalFeature(FeatureName).Should().NotBeNull();
    }

    [Fact]
    public void RefreshingAssignmentsReplacesTheDocumentsInsteadOfClearingThemInPlace()
    {
        const string FeatureName = "test-feature";

        var service = new InMemoryStickyBucketService();
        service.SaveAssignments(new StickyAssignmentsDocument(
            "id",
            "user-1",
            new Dictionary<string, string> { [$"{FeatureName}__0"] = "1" }));

        var callerDocuments = new Dictionary<string, StickyAssignmentsDocument>();

        var context = new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { [FeatureName] = CreateExperimentFeature() },
            StickyBucketService = service,
            StickyBucketAssignmentDocs = callerDocuments
        };

        var growthBook = new GrowthBook(context);

        // The refresh publishes a new dictionary rather than mutating the one it was handed, so the caller's
        // instance is left exactly as they passed it in.
        callerDocuments.Should().BeEmpty("because the refreshed set is published as a replacement, not written into the caller's dictionary");
        growthBook.EvalFeature(FeatureName).ExperimentResult.StickyBucketUsed.Should().BeTrue();
    }
}
