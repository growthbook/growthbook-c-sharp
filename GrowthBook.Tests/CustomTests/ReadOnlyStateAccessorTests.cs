using System;
using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

/// <summary>
/// Covers the read-only accessors for the loaded state. The mutable <c>Features</c> and
/// <c>Experiments</c> properties stay as they are; these give a consumer - a debug or health endpoint,
/// an exporter - a way to read what the SDK loaded without being handed the collections it evaluates
/// against.
/// </summary>
public class ReadOnlyStateAccessorTests : UnitTest
{
    private static Feature Flag(bool value) => new Feature { DefaultValue = value };

    private static GrowthBook NewInstance() => new GrowthBook(new Context
    {
        Attributes = JObject.FromObject(new { id = "user-1" }),
        Features = new Dictionary<string, Feature> { ["first"] = Flag(true), ["second"] = Flag(false) },
        Experiments = new List<Experiment> { new Experiment { Key = "exp", Variations = new JArray(0, 1) } }
    });

    [Fact]
    public void BothAccessorsAreEmptyRatherThanNullBeforeAnythingIsLoaded()
    {
        using var growthBook = new GrowthBook(new Context());

        growthBook.GetFeatures().Should().NotBeNull().And.BeEmpty();
        growthBook.GetExperiments().Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void TheyReturnWhatWasLoaded()
    {
        using var growthBook = NewInstance();

        growthBook.GetFeatures().Keys.Should().BeEquivalentTo(new[] { "first", "second" });
        growthBook.GetExperiments().Should().ContainSingle().Which.Key.Should().Be("exp");
    }

    [Fact]
    public void TheReturnedFeatureMapCannotBeMutated()
    {
        using var growthBook = NewInstance();

        var features = growthBook.GetFeatures();

        // IReadOnlyDictionary has no mutators, so the only way in is casting back - which the
        // ReadOnlyDictionary wrapper has to refuse rather than quietly allow.
        Action mutate = () => ((IDictionary<string, Feature>)features).Clear();

        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void TheReturnedExperimentListCannotBeMutated()
    {
        using var growthBook = NewInstance();

        var experiments = growthBook.GetExperiments();

        // Same reasoning as the feature map: IReadOnlyList has no mutators, so the only way in is a cast
        // back, and the wrapper has to refuse it rather than quietly allow it.
        Action clear = () => ((IList<Experiment>)experiments).Clear();
        Action add = () => ((IList<Experiment>)experiments).Add(new Experiment { Key = "injected" });

        clear.Should().Throw<NotSupportedException>();
        add.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void TheEmptyExperimentListCannotBeMutatedEither()
    {
        using var growthBook = new GrowthBook(new Context());

        Action add = () => ((IList<Experiment>)growthBook.GetExperiments()).Add(new Experiment { Key = "injected" });

        add.Should().Throw<NotSupportedException>("the before-load path returns the same kind of view as the loaded one");
    }

    [Fact]
    public void MutatingTheInstanceDoesNotChangeAnAlreadyReturnedSnapshot()
    {
        using var growthBook = NewInstance();

        var features = growthBook.GetFeatures();
        var experiments = growthBook.GetExperiments();

        growthBook.Features["third"] = Flag(true);
        growthBook.Experiments.Add(new Experiment { Key = "later", Variations = new JArray(0, 1) });

        features.Should().HaveCount(2, "a snapshot is a point in time, not a live view");
        experiments.Should().HaveCount(1);

        growthBook.GetFeatures().Should().HaveCount(3, "while a fresh call reflects the change");
        growthBook.GetExperiments().Should().HaveCount(2);
    }

    [Fact]
    public void ClearingTheInstanceStateThroughTheSnapshotIsImpossible()
    {
        using var growthBook = NewInstance();

        Action mutate = () => ((IDictionary<string, Feature>)growthBook.GetFeatures()).Remove("first");

        mutate.Should().Throw<NotSupportedException>();
        growthBook.IsOn("first").Should().BeTrue("because the instance's own definitions are untouched");
    }

    [Fact]
    public void TheMutablePropertiesStillWorkAsBefore()
    {
        using var growthBook = NewInstance();

        growthBook.Features["third"] = Flag(true);

        growthBook.IsOn("third").Should().BeTrue("because this accessor is additive - it does not lock the properties down");

        growthBook.Features = new Dictionary<string, Feature> { ["only"] = Flag(true) };

        growthBook.GetFeatures().Keys.Should().BeEquivalentTo(new[] { "only" });
    }

    [Fact]
    public void TheyAreReachableThroughTheInterface()
    {
        using IGrowthBook growthBook = NewInstance();

        growthBook.GetFeatures().Should().HaveCount(2, "a DI-injected consumer only sees IGrowthBook");
        growthBook.GetExperiments().Should().HaveCount(1);
    }
}
