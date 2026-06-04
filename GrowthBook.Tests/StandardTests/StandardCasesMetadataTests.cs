using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using System.Text.Json.Nodes;
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

    private static readonly HashSet<string> IntentionallyUntestedCategories = new HashSet<string>
    {
        "urlRedirect"
    };

    [Fact]
    public void StandardCasesSpecVersionIsCurrent()
    {
        var standardCases = LoadStandardCases();

        standardCases["specVersion"]?.ToString().Should().Be("0.7.1");
    }

    [Fact]
    public void StandardCaseCategoriesAreExplicitlyHandled()
    {
        var standardCases = LoadStandardCases();
        var categories = standardCases
            .Where(kvp => kvp.Value is JsonArray)
            .Select(kvp => kvp.Key)
            .ToList();

        var handledCategories = TestedCategories.Concat(IntentionallyUntestedCategories).ToHashSet();

        categories.Should().OnlyContain(
            category => handledCategories.Contains(category),
            "new standard-case categories should be wired into tests or explicitly documented as intentionally untested");
    }

    private static JsonObject LoadStandardCases()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GrowthBook.Tests.Json.standard-cases.json");

        if (stream == null)
        {
            throw new InvalidOperationException("Unable to load embedded standard-cases.json");
        }

        using var reader = new StreamReader(stream);

        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }
}
