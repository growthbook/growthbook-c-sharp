using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// The copies a plugin is handed. They are written by hand, because a serializer round trip cost tens of
/// microseconds on every flag check, so these tests make sure a hand-written copy stays complete as the types grow.
/// </summary>
public class PluginEventCopyTests
{
    [Fact]
    public void ACopySaysTheSameAndSharesNothingMutable()
    {
        var original = FullyPopulatedResult();

        var copy = original.Copy();

        JToken.DeepEquals(JToken.FromObject(original), JToken.FromObject(copy)).Should().BeTrue("a copy says the same as the original");
        AssertDetached(original, copy, nameof(FeatureResult));
    }

    /// <summary>
    /// The test above only checks what the sample sets. A property added to one of these types later has to be set
    /// here too, at which point that test checks the copy covers it.
    /// </summary>
    [Fact]
    public void TheSampleSetsEveryPropertyACopyCouldShare()
    {
        AssertPopulated(FullyPopulatedResult(), nameof(FeatureResult));
    }

    private static FeatureResult FullyPopulatedResult() => new FeatureResult
    {
        Value = JObject.Parse(@"{ ""nested"": { ""a"": 1 } }"),
        Source = FeatureResult.SourceId.Experiment,
        Experiment = new Experiment
        {
            Key = "exp",
            Variations = new JArray(new JObject { ["v"] = 1 }, new JObject { ["v"] = 2 }),
            Weights = new List<double> { 0.5, 0.5 },
            Coverage = 1,
            Ranges = new List<BucketRange> { new BucketRange(0, 0.5) },
            Condition = JObject.Parse(@"{ ""id"": { ""$exists"": true } }"),
            ParentConditions = new List<ParentCondition> { new ParentCondition { Id = "parent", Condition = JObject.Parse(@"{ ""value"": true }"), Gate = true } },
            Namespace = new Namespace("ns", 0, 1),
            Force = 1,
            FallbackAttribute = "deviceId",
            Meta = new List<VariationMeta> { new VariationMeta { Key = "0", Name = "control" } },
            Filters = new List<Filter> { new Filter { Seed = "seed", Ranges = new[] { new BucketRange(0, 1) } } },
            Seed = "seed",
            Name = "name",
            Phase = "1",
            UrlPatterns = new List<UrlPattern> { new UrlPattern { Type = "simple", Pattern = "/x" } },
            CustomFields = new Dictionary<string, object> { ["owner"] = JObject.Parse(@"{ ""team"": ""x"" }") }
        },
        ExperimentResult = new ExperimentResult
        {
            InExperiment = true,
            VariationId = 1,
            Value = JObject.Parse(@"{ ""v"": 2 }"),
            HashAttribute = "id",
            HashValue = "user-1",
            FeatureId = "flag",
            Key = "1",
            Name = "treatment"
        }
    };

    private static bool IsShareable(object value) =>
        value is null || value.GetType().IsValueType || value is string || value is Namespace;

    private static IEnumerable<PropertyInfo> Properties(object value) =>
        value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    private static void AssertDetached(object original, object copy, string path)
    {
        if (IsShareable(original))
        {
            return;
        }

        ReferenceEquals(original, copy).Should().BeFalse($"{path} has to be copied, not shared");

        switch (original)
        {
            case JToken _:
                return;
            case IDictionary<string, object> fields:
                foreach (var field in fields)
                {
                    AssertDetached(field.Value, ((IDictionary<string, object>)copy)[field.Key], $"{path}[{field.Key}]");
                }

                return;
            case IEnumerable items:
                var copied = ((IEnumerable)copy).Cast<object>().ToList();
                var index = 0;

                foreach (var item in items)
                {
                    AssertDetached(item, copied[index], $"{path}[{index}]");
                    index++;
                }

                return;
        }

        foreach (var property in Properties(original))
        {
            AssertDetached(property.GetValue(original), property.GetValue(copy), $"{path}.{property.Name}");
        }
    }

    private static void AssertPopulated(object sample, string path)
    {
        if (IsShareable(sample) || sample is JToken)
        {
            return;
        }

        if (sample is IEnumerable items && !(sample is IDictionary))
        {
            foreach (var item in items)
            {
                AssertPopulated(item, $"{path}[]");
            }

            return;
        }

        foreach (var property in Properties(sample).Where(p => p.CanWrite && !p.PropertyType.IsValueType && p.PropertyType != typeof(string)))
        {
            var value = property.GetValue(sample);
            value.Should().NotBeNull($"{path}.{property.Name} could be shared by a copy, so the sample has to set it");
            AssertPopulated(value, $"{path}.{property.Name}");
        }
    }
}
