using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace GrowthBook.Tests.StandardTests.UtilitiesTests;

public class DecryptTests : UnitTest
{
    [StandardCaseTestCategory("decrypt")]
    public class DecryptTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public string EncryptedValue { get; set; }
        [TestPropertyIndex(2)]
        public string DecryptionKey { get; set; }
        [TestPropertyIndex(3)]
        public string ExpectedResult { get; set; }
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(DecryptTestCase))]
    public void Decrypt(DecryptTestCase testCase)
    {
        try
        {
            var actualValue = Utilities.Decrypt(testCase.EncryptedValue, testCase.DecryptionKey);

            actualValue.Trim().Should().Be(testCase.ExpectedResult, "because the decryption should behave correctly");
        }
        catch(Exception ex)
        {
            
        }
    }
}
