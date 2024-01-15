using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Utilities;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class HashTests : UnitTest
{
    [StandardCaseTestCategory("hash")]
    public class HashTestCase
    {
        [TestPropertyIndex(0)]
        public string Seed { get; set; }
        [TestPropertyIndex(1)]
        public string ValueToHash { get; set; }
        [TestPropertyIndex(2)]
        public int HashVersionToUse { get; set; }
        [TestPropertyIndex(3)]
        public float? ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(HashTestCase))]
    public void Hash(HashTestCase testCase)
    {
        var actualResult = HashUtilities.Hash(testCase.Seed, testCase.ValueToHash, testCase.HashVersionToUse);

        actualResult.Should().Be(testCase.ExpectedResult, "because the hashing algorithm should be deterministic");
    }
}
