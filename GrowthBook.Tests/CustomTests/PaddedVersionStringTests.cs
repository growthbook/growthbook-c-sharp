using System.Diagnostics;
using FluentAssertions;
using GrowthBook.Providers;
using Xunit;

namespace GrowthBook.Tests.CustomTests;

public class PaddedVersionStringTests
{
    [Theory]
    [InlineData("v1.2.3-rc.1+build123", "    1-    2-    3-rc-    1")]
    [InlineData("1.2.3", "    1-    2-    3-~")]
    [InlineData("v1.2.3+build+more.1-2", "    1-    2-    3-~")]
    [InlineData("1.2.3+", "    1-    2-    3-~")]
    // Build metadata is everything after the first `+`, even across a newline
    [InlineData("1.2.3+build\nx", "    1-    2-    3-~")]
    [InlineData("1.2.3+build\n", "    1-    2-    3-~")]
    [InlineData("vv1.2.3", "v1-    2-    3-~")]
    [InlineData("+build", "")]
    [InlineData("", "    0")]
    // A part with a trailing newline is not numeric, so it is not padded
    [InlineData("1.2.3\n", "    1-    2-3\n-~")]
    [InlineData("1.0.0-3\n", "    1-    0-    0-3\n")]
    public void StripsLeadingVAndBuildMetadata(string input, string expected)
    {
        ConditionEvaluationProvider.PaddedVersionString(input).Should().Be(expected);
    }

    [Fact]
    public void IsLinearOnManyPluses()
    {
        // The old `(^v|\+.*$)` regex took several seconds on this input
        var input = new string('+', 100_000) + "\nx";
        var stopwatch = Stopwatch.StartNew();

        ConditionEvaluationProvider.PaddedVersionString(input).Should().BeEmpty();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
}
