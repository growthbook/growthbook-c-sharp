using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class InNamespaceTests : UnitTest
{
    [StandardCaseTestCategory("inNamespace")]
    public class InNamespaceTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public string Id { get; set; }
        [TestPropertyIndex(2)]
        public Namespace Namespace { get; set; }
        [TestPropertyIndex(3)]
        public bool ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(InNamespaceTestCase))]
    public void InNamespace(InNamespaceTestCase testCase)
    {
        var actualResult = Utilities.InNamespace(testCase.Id, testCase.Namespace);

        actualResult.Should().Be(testCase.ExpectedResult, "because the namespace logic should be correct");
    }
}
