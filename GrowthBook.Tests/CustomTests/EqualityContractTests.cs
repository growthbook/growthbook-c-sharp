using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the equality contract on the public types that override <see cref="object.Equals(object)"/>.
/// Each of them used to throw from <c>GetHashCode</c>, so putting one in a set, using it as a
/// dictionary key or deduplicating a sequence of them failed at runtime with no compiler warning.
/// </summary>
public class EqualityContractTests
{
    public static IEnumerable<object[]> EquatableTypes() => new[]
    {
        new object[] { typeof(BucketRange) },
        new object[] { typeof(Namespace) },
        new object[] { typeof(Experiment) },
        new object[] { typeof(ExperimentResult) },
        new object[] { typeof(FeatureResult) }
    };

    private static object Sample(Type type)
    {
        if (type == typeof(BucketRange)) return new BucketRange(0.1d, 0.6d);
        if (type == typeof(Namespace)) return new Namespace("ns", 0.1d, 0.6d);
        if (type == typeof(Experiment)) return new Experiment { Key = "exp", Coverage = 1d, Weights = new[] { 0.5d, 0.5d } };
        if (type == typeof(ExperimentResult)) return new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1, Value = new JValue(true) };
        if (type == typeof(FeatureResult)) return new FeatureResult { Source = "experiment", Value = new JValue(true) };

        throw new ArgumentOutOfRangeException(nameof(type), type, "no sample for this type");
    }

    [Theory]
    [MemberData(nameof(EquatableTypes))]
    public void GetHashCodeDoesNotThrow(Type type)
    {
        Action hash = () => Sample(type).GetHashCode();

        hash.Should().NotThrow(type.Name);
    }

    [Theory]
    [MemberData(nameof(EquatableTypes))]
    public void EqualInstancesHashEqually(Type type)
    {
        var first = Sample(type);
        var second = Sample(type);

        first.Should().Be(second, "the two samples are built identically");
        first.GetHashCode().Should().Be(second.GetHashCode(),
            "equal instances hashing differently is the whole defect - a set would hold both");
    }

    [Theory]
    [MemberData(nameof(EquatableTypes))]
    public void EqualsAgainstNullIsFalseRatherThanThrowing(Type type)
    {
        var instance = Sample(type);

        Action compare = () => instance.Equals(null);

        compare.Should().NotThrow(type.Name);
        instance.Equals(null).Should().BeFalse(type.Name);
    }

    [Theory]
    [MemberData(nameof(EquatableTypes))]
    public void EqualsAgainstAnUnrelatedTypeIsFalse(Type type)
    {
        Sample(type).Equals("not a growthbook type").Should().BeFalse(type.Name);
    }

    [Fact]
    public void ADeduplicatedSequenceCollapsesEqualResults()
    {
        var results = new[]
        {
            new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1 },
            new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1 },
            new ExperimentResult { HashAttribute = "id", HashValue = "user-2", VariationId = 0 }
        };

        results.Distinct().Should().HaveCount(2, "because the first two are equal and now hash alike");
    }

    [Fact]
    public void ASetHoldsOneEntryPerEqualExperiment()
    {
        var experiments = new HashSet<Experiment>
        {
            new Experiment { Key = "exp", Coverage = 1d },
            new Experiment { Key = "exp", Coverage = 1d },
            new Experiment { Key = "other", Coverage = 1d }
        };

        experiments.Should().HaveCount(2);
    }

    [Fact]
    public void ABucketRangeWorksAsADictionaryKey()
    {
        var counts = new Dictionary<BucketRange, int>
        {
            [new BucketRange(0d, 0.5d)] = 1
        };

        counts[new BucketRange(0d, 0.5d)] = counts[new BucketRange(0d, 0.5d)] + 1;

        counts.Should().HaveCount(1, "because an equal range has to resolve to the same bucket");
        counts[new BucketRange(0d, 0.5d)].Should().Be(2);
    }

    [Fact]
    public void ResultsThatDifferOnlyByJsonValueRemainUnequal()
    {
        // The JToken fields are left out of the hash, so these two collide. They must still compare
        // unequal - a collision is a performance detail, equality is not.
        var withTrue = new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1, Value = new JValue(true) };
        var withFalse = new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1, Value = new JValue(false) };

        withTrue.Should().NotBe(withFalse);

        new[] { withTrue, withFalse }.Distinct().Should().HaveCount(2, "a hash collision must not merge unequal values");
    }

    [Fact]
    public void ResultsWhoseJsonValuesAreDeepEqualHashAlike()
    {
        var first = new ExperimentResult { HashAttribute = "id", HashValue = "user-1", Value = JObject.Parse("{\"a\":1,\"b\":2}") };
        var second = new ExperimentResult { HashAttribute = "id", HashValue = "user-1", Value = JObject.Parse("{\"b\":2,\"a\":1}") };

        first.Should().Be(second, "DeepEquals ignores property ordering");
        first.GetHashCode().Should().Be(second.GetHashCode(),
            "which is exactly why the hash cannot be derived from a serialized form of the token");
    }

    [Fact]
    public void AFeatureResultHashesThroughItsNestedExperimentAndResult()
    {
        FeatureResult Build() => new FeatureResult
        {
            Source = "experiment",
            Experiment = new Experiment { Key = "exp", Coverage = 1d },
            ExperimentResult = new ExperimentResult { HashAttribute = "id", HashValue = "user-1", VariationId = 1 }
        };

        Build().GetHashCode().Should().Be(Build().GetHashCode());

        var different = Build();
        different.Experiment = new Experiment { Key = "other", Coverage = 1d };

        different.Should().NotBe(Build());
    }
}
