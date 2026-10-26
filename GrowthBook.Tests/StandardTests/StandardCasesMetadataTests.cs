using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests;

public class StandardCasesMetadataTests
{
    private static readonly HashSet<string> TestedCategories = new HashSet<string>
    {
        "chooseVariation",
        "decrypt",
        "evalCondition",
        "feature",
        "getBucketRange",
        "getEqualWeights",
        "getQueryStringOverride",
        "hash",
        "inNamespace",
        "run",
        "stickyBucket"
    };

    /// <summary>
    /// Categories nested inside an object rather than listed at the top level.
    /// </summary>
    private static readonly HashSet<string> TestedNestedCategories = new HashSet<string>
    {
        "savedGroupReferencesV2.evalCondition",
        "savedGroupReferencesV2.feature",
        "savedGroupReferencesV2.run"
    };

    private static readonly HashSet<string> IntentionallyUntestedCategories = new HashSet<string>
    {
        "urlRedirect",
        "pruneCondition",
        "reducePayload",
        // Contextual bandits aren't implemented in this SDK yet
        "contextualBandit"
    };

    [Fact]
    public void StandardCasesSpecVersionIsCurrent()
    {
        var standardCases = LoadStandardCases();

        standardCases["specVersion"]?.ToString().Should().Be("0.11.2");
    }

    [Fact]
    public void StandardCaseCategoriesAreExplicitlyHandled()
    {
        var standardCases = LoadStandardCases();
        var categories = standardCases.Properties()
            .Where(property => property.Value.Type == JTokenType.Array)
            .Select(property => property.Name)
            .Concat(standardCases.Properties()
                .Where(property => property.Value.Type == JTokenType.Object)
                .SelectMany(property => ((JObject)property.Value).Properties()
                    .Where(nested => nested.Value.Type == JTokenType.Array)
                    .Select(nested => $"{property.Name}.{nested.Name}")))
            .ToList();

        var handledCategories = TestedCategories
            .Concat(TestedNestedCategories)
            .Concat(IntentionallyUntestedCategories)
            .ToHashSet();

        categories.Should().OnlyContain(
            category => handledCategories.Contains(category),
            "new standard-case categories should be wired into tests or explicitly documented as intentionally untested");
    }

    private static JObject LoadStandardCases()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GrowthBook.Tests.Json.standard-cases.json");

        if (stream == null)
        {
            throw new InvalidOperationException("Unable to load embedded standard-cases.json");
        }

        using var reader = new StreamReader(stream);

        return JObject.Parse(reader.ReadToEnd());
    }
}
