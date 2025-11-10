using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Services;
using Xunit;
using Xunit.Abstractions;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class StickyBucketTests : UnitTest
{
    private readonly ITestOutputHelper _output;

    public StickyBucketTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [StandardCaseTestCategory("stickyBucket")]
    public class StickyBucketTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }

        [TestPropertyIndex(1)]
        public Context Context { get; set; }

        [TestPropertyIndex(2)]
        public StickyAssignmentsDocument[] PreExistingAssignmentDocs { get; set; } = Array.Empty<StickyAssignmentsDocument>();

        [TestPropertyIndex(3)]
        public string FeatureName { get; set; }

        [TestPropertyIndex(4)]
        public JsonNode ExpectedResult { get; set; }

        [TestPropertyIndex(5)]
        public Dictionary<string, StickyAssignmentsDocument> ExpectedAssignmentDocs { get; set; } = new Dictionary<string, StickyAssignmentsDocument>();
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(StickyBucketTestCase))]
    public void Run(StickyBucketTestCase testCase)
    {
        _output.WriteLine($"Running test: {testCase.TestName}");
        _output.WriteLine($"PreExisting docs count: {testCase.PreExistingAssignmentDocs?.Length ?? 0}");

        var service = new InMemoryStickyBucketService();

        testCase.Context.StickyBucketService = service;

        if (testCase.PreExistingAssignmentDocs != null && testCase.PreExistingAssignmentDocs.Any())
        {
            foreach (var doc in testCase.PreExistingAssignmentDocs)
            {
                _output.WriteLine($"Loading doc: {doc.AttributeName}||{doc.AttributeValue}");
                _output.WriteLine($"  Assignments: {string.Join(", ", doc.Assignments?.Select(kvp => $"{kvp.Key}={kvp.Value}") ?? Array.Empty<string>())}");

                service.SaveAssignments(doc);
            }

            testCase.Context.StickyBucketAssignmentDocs = testCase.PreExistingAssignmentDocs
                .ToDictionary(x => x.FormattedAttribute);
        }
        else
        {
            testCase.Context.StickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument>();
        }

        var gb = new GrowthBook(testCase.Context);
        var result = gb.EvalFeature(testCase.FeatureName);

        var actualResult = JsonSerializer.SerializeToNode(
            result.ExperimentResult,
            GrowthBookJsonContext.Default.ExperimentResult
        );

        _output.WriteLine($"Expected: {testCase.ExpectedResult?.ToJsonString()}");
        _output.WriteLine($"Actual: {actualResult?.ToJsonString()}");

        if (testCase.ExpectedResult is JsonObject expectedObj)
        {
            var actualObj = actualResult as JsonObject;

            var keysToRemove = actualObj?.Where(kvp => !expectedObj.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove ?? Enumerable.Empty<string>())
            {
                actualObj?.Remove(key);
            }

            JsonNode.DeepEquals(actualObj, expectedObj).Should().BeTrue(
                $"Test: {testCase.TestName}\n\nExpected:\n{expectedObj.ToJsonString()}\n\nActual:\n{actualObj?.ToJsonString()}"
            );
        }
        else if (testCase.ExpectedResult == null)
        {
            actualResult.Should().BeNull($"Test: {testCase.TestName}");
        }
        else
        {
            actualResult.ToString().Should().Be(testCase.ExpectedResult.ToString(), $"Test: {testCase.TestName}");
        }

        var storedDocuments = service.GetAllAssignments(testCase.ExpectedAssignmentDocs.Keys);

        _output.WriteLine($"\nExpected docs count: {testCase.ExpectedAssignmentDocs.Count}");
        _output.WriteLine($"Stored docs count: {storedDocuments.Count}");

        foreach (var kvp in testCase.ExpectedAssignmentDocs)
        {
            _output.WriteLine($"Expected doc: {kvp.Key}");
            _output.WriteLine($"  Assignments: {string.Join(", ", kvp.Value.Assignments?.Select(a => $"{a.Key}={a.Value}") ?? Array.Empty<string>())}");
        }

        foreach (var kvp in storedDocuments)
        {
            _output.WriteLine($"Stored doc: {kvp.Key}");
            _output.WriteLine($"  Assignments: {string.Join(", ", kvp.Value.Assignments?.Select(a => $"{a.Key}={a.Value}") ?? Array.Empty<string>())}");
        }

        storedDocuments.Should().BeEquivalentTo(
            testCase.ExpectedAssignmentDocs,
            options => options
                .WithStrictOrdering()
                .ComparingByMembers<StickyAssignmentsDocument>(),
            $"Test: {testCase.TestName} - Documents should have been stored correctly"
        );
    }
}
