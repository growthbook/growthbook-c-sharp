using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using GrowthBook.Extensions;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class VersionCompareTests : UnitTest
{
    public class VersionCompareTestCase
    {
        [TestPropertyIndex(0)]
        public string Version { get; set; }
        [TestPropertyIndex(1)]
        public string OtherVersion { get; set; }
        [TestPropertyIndex(2)]
        public bool ExpectedResult { get; set; }
    }

    [StandardCaseTestCategory("versionCompare.lt")]
    public class VersionCompareLessThanTestCase : VersionCompareTestCase { }

    [StandardCaseTestCategory("versionCompare.gt")]
    public class VersionCompareGreaterThanTestCase : VersionCompareTestCase { }

    [StandardCaseTestCategory("versionCompare.eq")]
    public class VersionCompareEqualTestCase : VersionCompareTestCase { }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(VersionCompareLessThanTestCase))]
    public void VersionCompareLessThan(VersionCompareLessThanTestCase testCase)
    {
        var versionString = testCase.Version.ToPaddedVersionString();
        var otherVersionString = testCase.OtherVersion.ToPaddedVersionString();

        var actualResult = string.CompareOrdinal(versionString, otherVersionString) < 0;

        actualResult.Should().Be(testCase.ExpectedResult, $"because '{versionString}' should be less than '{otherVersionString}'");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(VersionCompareGreaterThanTestCase))]
    public void VersionCompareGreaterThan(VersionCompareGreaterThanTestCase testCase)
    {
        var versionString = testCase.Version.ToPaddedVersionString();
        var otherVersionString = testCase.OtherVersion.ToPaddedVersionString();

        var actualResult = string.CompareOrdinal(versionString, otherVersionString) > 0;

        actualResult.Should().Be(testCase.ExpectedResult, $"because '{versionString}' should be greater than '{otherVersionString}'");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(VersionCompareEqualTestCase))]
    public void VersionCompareEqual(VersionCompareEqualTestCase testCase)
    {
        var versionString = testCase.Version.ToPaddedVersionString();
        var otherVersionString = testCase.OtherVersion.ToPaddedVersionString();

        var actualResult = string.CompareOrdinal(versionString, otherVersionString) == 0;

        actualResult.Should().Be(testCase.ExpectedResult, $"because '{versionString}' should be less than '{otherVersionString}'");
    }
}
