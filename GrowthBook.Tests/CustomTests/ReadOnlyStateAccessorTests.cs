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
[Collection("SerializerSettings")]
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

    /// <summary>
    /// netstandard2.0 has no default interface members, so a member added to <see cref="IGrowthBook"/>
    /// stops every hand-written implementation compiling - a test double or a decorator in a consumer's
    /// own code. These are declared on the class instead, and this pins that they stay there.
    /// </summary>
    [Fact]
    public void TheyAreNotDeclaredOnTheInterfaceConsumersImplement()
    {
        typeof(IGrowthBook).GetMethod(nameof(GrowthBook.GetFeatures)).Should().BeNull(
            "an existing implementation of IGrowthBook must keep compiling without being rewritten");

        typeof(IGrowthBook).GetMethod(nameof(GrowthBook.GetExperiments)).Should().BeNull();

        typeof(GrowthBook).GetMethod(nameof(GrowthBook.GetFeatures)).Should().NotBeNull(
            "but the concrete type still offers them");
    }

    /// <summary>
    /// A read-only container over the instance's own objects is not read-only state. Editing what it
    /// holds changes what the instance evaluates, which is the thing these accessors exist to prevent.
    /// </summary>
    [Fact]
    public void EditingASnapshottedFeatureDoesNotChangeWhatIsEvaluated()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["first"] = new Feature { DefaultValue = true } }
        });

        growthBook.IsOn("first").Should().BeTrue();

        growthBook.GetFeatures()["first"].DefaultValue = false;

        growthBook.IsOn("first").Should().BeTrue("the snapshot is a copy, so editing it cannot turn a flag off");
        growthBook.GetFeatures()["first"].DefaultValue.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void EditingASnapshottedFeaturesRulesDoesNotChangeWhatIsEvaluated()
    {
        var feature = new Feature
        {
            DefaultValue = false,
            Rules = new List<FeatureRule> { new FeatureRule { Force = true } }
        };

        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Features = new Dictionary<string, Feature> { ["forced"] = feature }
        });

        growthBook.IsOn("forced").Should().BeTrue();

        growthBook.GetFeatures()["forced"].Rules.Clear();

        growthBook.IsOn("forced").Should().BeTrue("the nested rules are copied too, not shared");
    }

    [Fact]
    public void EditingASnapshottedExperimentDoesNotChangeTheStoredOne()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" }),
            Experiments = new List<Experiment>
            {
                new Experiment { Key = "exp", Variations = new JArray(0, 1) }
            }
        });

        growthBook.GetExperiments()[0].Variations = new JArray(9, 9, 9);
        growthBook.GetExperiments()[0].Key = "renamed";

        var stored = growthBook.GetExperiments()[0];

        stored.Key.Should().Be("exp");
        stored.Variations.Should().HaveCount(2, "the stored experiment is untouched by edits to a snapshot");
    }

    /// <summary>
    /// A caller can hand the SDK a case-insensitive dictionary, and evaluation honours it. A snapshot
    /// taken under the default rules would reject keys the instance itself accepts.
    /// </summary>
    [Fact]
    public void ASnapshotKeepsTheKeyComparerTheCallerChose()
    {
        using var growthBook = new GrowthBook(new Context
        {
            Attributes = JObject.FromObject(new { id = "user-1" })
        });

        // Assigned to the property rather than passed on the Context: the constructor copies with
        // ToDictionary, which drops the comparer, so only a direct assignment keeps it.
        growthBook.Features = new Dictionary<string, Feature>(StringComparer.OrdinalIgnoreCase)
        {
            ["First"] = new Feature { DefaultValue = true }
        };

        growthBook.IsOn("first").Should().BeTrue("the instance itself accepts the key either way");

        var snapshot = growthBook.GetFeatures();

        snapshot.ContainsKey("first").Should().BeTrue("the instance accepts this key, so the snapshot has to as well");
        snapshot["FIRST"].Should().NotBeNull();
    }

    /// <summary>
    /// A host application can set <see cref="Newtonsoft.Json.JsonConvert.DefaultSettings"/>, and a
    /// serializer built with <c>CreateDefault</c> inherits them. Under
    /// <c>DefaultValueHandling.Ignore</c> a copy of an experiment with <c>Active = false</c> loses the
    /// property, and the new object takes the initializer's <c>true</c> - a disabled experiment reported
    /// as enabled, with nothing to hint that it happened.
    /// </summary>
    /// <remarks>
    /// In its own collection, because DefaultSettings is global and the rest of the suite runs in
    /// parallel. Restored in a finally so a failure here cannot leak into another run.
    /// </remarks>
    [Fact]
    public void AHostsOwnSerializerSettingsDoNotChangeWhatASnapshotReports()
    {
        var previous = Newtonsoft.Json.JsonConvert.DefaultSettings;

        try
        {
            Newtonsoft.Json.JsonConvert.DefaultSettings = () => new Newtonsoft.Json.JsonSerializerSettings
            {
                DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore
            };

            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { id = "user-1" }),
                Experiments = new List<Experiment>
                {
                    new Experiment { Key = "disabled-experiment", Active = false, Variations = new JArray(false, true) }
                }
            });

            var snapshot = growthBook.GetExperiments();

            snapshot.Should().ContainSingle();
            snapshot[0].Active.Should().BeFalse(
                "the snapshot reports what the instance holds, whatever the host configured for its own serialization");
            snapshot[0].Key.Should().Be("disabled-experiment");
        }
        finally
        {
            Newtonsoft.Json.JsonConvert.DefaultSettings = previous;
        }
    }
}
