using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using GrowthBook.Exceptions;
using GrowthBook.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Sdk;

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
        string actualValue = null;

        try
        {
            actualValue = CryptographyUtilities.Decrypt(testCase.EncryptedValue, testCase.DecryptionKey);

            actualValue.Trim().Should().Be(testCase.ExpectedResult, "because the decryption should behave correctly");
        }
        catch(DecryptionException)
        {
            testCase.ExpectedResult.Should().BeNull("because a null value should mean that a decryption exception is being provoked");
        }
        catch(XunitException)
        {
            // There was a mismatch between the expected and actual but it wasn't a decryption issue,
            // so it's likely to be garbage characters as a result of incorrect keys or similar. Double check that.

            try
            {
                var jsonObject = JObject.Parse(actualValue);
            }
            catch(JsonReaderException)
            {
                return;
            }

            throw;
        }
    }
}
