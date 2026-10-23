using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Json.NET reads ISO 8601 strings as dates by default, which loses their original text. These tests check
    /// that a timestamp compares the same way whether each side arrived as a string or was read as a date.
    /// </summary>
    public class TimestampConditionTests
    {
        private readonly ConditionEvaluationProvider _provider;

        public TimestampConditionTests()
        {
            _provider = new ConditionEvaluationProvider(new NullLogger<ConditionEvaluationProvider>());
        }

        /// <summary>
        /// Builds a condition whose timestamp Json.NET reads as a date, as <c>JObject.Parse</c> does by default.
        /// </summary>
        private static JObject DateCondition(string op, string timestamp) =>
            JObject.Parse($@"{{ ""d"": {{ ""{op}"": ""{timestamp}"" }} }}");

        [Theory]
        [InlineData("2026-10-20T00:00:00.000Z")]
        [InlineData("2026-10-20T00:00:00Z")]
        [InlineData("2026-10-20T00:00:00.123456Z")]
        [InlineData("2026-10-20T02:00:00+02:00")]
        [InlineData("2026-10-20T02:00:00.500-05:00")]
        public void AStringAttributeEqualToADateConditionIsNotPastAStrictCutoff(string timestamp)
        {
            var attributes = JObject.FromObject(new { d = timestamp });

            attributes["d"].Type.Should().Be(JTokenType.String);
            DateCondition("$lt", timestamp)["d"]["$lt"].Type.Should().Be(JTokenType.Date);

            _provider.EvalCondition(attributes, DateCondition("$lt", timestamp)).Should().BeFalse();
            _provider.EvalCondition(attributes, DateCondition("$gt", timestamp)).Should().BeFalse();
            _provider.EvalCondition(attributes, DateCondition("$lte", timestamp)).Should().BeTrue();
            _provider.EvalCondition(attributes, DateCondition("$gte", timestamp)).Should().BeTrue();
            _provider.EvalCondition(attributes, DateCondition("$eq", timestamp)).Should().BeTrue();
            _provider.EvalCondition(attributes, DateCondition("$ne", timestamp)).Should().BeFalse();
            _provider.EvalCondition(attributes, JObject.Parse($@"{{ ""d"": ""{timestamp}"" }}")).Should().BeTrue();
            _provider.EvalCondition(attributes, JObject.Parse($@"{{ ""d"": {{ ""$in"": [""{timestamp}""] }} }}")).Should().BeTrue();
        }

        /// <summary>
        /// Reads JSON the way the SDK reads a features payload, keeping timestamps as strings.
        /// </summary>
        private static JObject ParseAsStrings(string json) =>
            (JObject)JToken.ReadFrom(new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None });

        public static IEnumerable<object[]> NestedTimestampConditions() => new[]
        {
            new object[] { @"{ ""d"": { ""$in"": [""2026-10-20T00:00:00Z""] } }", true },
            new object[] { @"{ ""d"": { ""$nin"": [""2026-10-20T00:00:00Z""] } }", false },
            new object[] { @"{ ""d"": { ""$in"": [""2026-10-21T00:00:00Z""] } }", false },
            new object[] { @"{ ""d"": { ""$nin"": [""2026-10-21T00:00:00Z""] } }", true },
            new object[] { @"{ ""d"": { ""$all"": [""2026-10-20T00:00:00Z""] } }", true },
            new object[] { @"{ ""d"": { ""$elemMatch"": { ""$eq"": ""2026-10-20T00:00:00Z"" } } }", true },
            new object[] { @"{ ""d"": [""2026-10-20T00:00:00Z"", ""x""] }", true },
            new object[] { @"{ ""d"": [""2026-10-20T00:00:00Z""] }", false },
            new object[] { @"{ ""o"": { ""at"": ""2026-10-20T00:00:00Z"", ""tags"": [""2026-10-20T00:00:00Z""] } }", true },
            new object[] { @"{ ""o"": { ""at"": ""2026-10-21T00:00:00Z"", ""tags"": [""2026-10-20T00:00:00Z""] } }", false },
            new object[] { @"{ ""d"": { ""$inGroup"": ""dates"" } }", true },
            new object[] { @"{ ""d"": { ""$notInGroup"": ""dates"" } }", false }
        };

        [Theory]
        [MemberData(nameof(NestedTimestampConditions))]
        public void DatesInsideArraysAndObjectsMatchTheSameTimestampAsAString(string condition, bool expected)
        {
            const string attributesJson = @"{ ""d"": [""2026-10-20T00:00:00Z"", ""x""], ""o"": { ""at"": ""2026-10-20T00:00:00Z"", ""tags"": [""2026-10-20T00:00:00Z""] } }";
            var savedGroups = ParseAsStrings(@"{ ""dates"": [""2026-10-20T00:00:00Z""] }");

            // Every pairing of a date read by Json.NET and a string must agree
            foreach (var attributes in new[] { JObject.Parse(attributesJson), ParseAsStrings(attributesJson) })
            {
                foreach (var parsedCondition in new[] { JObject.Parse(condition), ParseAsStrings(condition) })
                {
                    _provider.EvalCondition(attributes, parsedCondition, savedGroups).Should().Be(expected,
                        "attribute dates: {0}, condition dates: {1}", attributes["d"][0].Type, parsedCondition.ToString());
                }
            }
        }

        [Fact]
        public void ADateConditionEqualToAStringAttributeIsNotPastAStrictCutoffEitherWay()
        {
            var attributes = JObject.Parse(@"{ ""d"": ""2026-10-20T00:00:00.000Z"" }");
            var condition = DateCondition("$lt", "2026-10-20T00:00:00.000Z");
            var stringCondition = (JObject)JToken.ReadFrom(new JsonTextReader(new System.IO.StringReader(
                @"{ ""d"": { ""$lt"": ""2026-10-20T00:00:00.000Z"" } }")) { DateParseHandling = DateParseHandling.None });

            attributes["d"].Type.Should().Be(JTokenType.Date);
            stringCondition["d"]["$lt"].Type.Should().Be(JTokenType.String);

            _provider.EvalCondition(attributes, condition).Should().BeFalse();
            _provider.EvalCondition(attributes, stringCondition).Should().BeFalse();
        }

        [Theory]
        [InlineData("2026-10-20T00:00:00.5Z", "2026-10-20T00:00:00Z")]
        [InlineData("2026-10-20T00:00:01Z", "2026-10-20T00:00:00.999Z")]
        [InlineData("2026-10-20T13:00:00Z", "2026-10-20T12:00:00Z")]
        public void LaterTimestampsCompareAsLater(string later, string earlier)
        {
            // Both forms: a string attribute, and one Json.NET read as a date
            foreach (var attributes in new[]
            {
                JObject.FromObject(new { d = later }),
                JObject.Parse($@"{{ ""d"": ""{later}"" }}")
            })
            {
                _provider.EvalCondition(attributes, DateCondition("$gt", earlier)).Should().BeTrue();
                _provider.EvalCondition(attributes, DateCondition("$lt", earlier)).Should().BeFalse();
            }
        }

        [Fact]
        public void ADateConditionDoesNotMatchAStringThatIsNotATimestamp()
        {
            var attributes = JObject.FromObject(new { d = "not a date" });

            _provider.EvalCondition(attributes, DateCondition("$eq", "2026-10-20T00:00:00Z")).Should().BeFalse();
        }

        [Fact]
        public void ADateReadByJsonNetHasTheStringType()
        {
            var attributes = JObject.Parse(@"{ ""d"": ""2026-10-20T00:00:00Z"" }");

            _provider.EvalCondition(attributes, JObject.Parse(@"{ ""d"": { ""$type"": ""string"" } }")).Should().BeTrue();
        }

        // AES-128 needs exactly 16 key bytes
        private const string DecryptionKey = "MDEyMzQ1Njc4OWFiY2RlZg==";

        private static string Encrypt(string plaintext)
        {
            using var aes = Aes.Create();

            aes.Key = Convert.FromBase64String(DecryptionKey);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipherBytes = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

            return $"{Convert.ToBase64String(aes.IV)}.{Convert.ToBase64String(cipherBytes)}";
        }

        [Fact]
        public void AnSdkLoadedPayloadKeepsTimestampsAsText()
        {
            const string payload = @"{""flag"":{""defaultValue"":false,""rules"":[{""condition"":{""d"":{""$lt"":""2026-10-20T00:00:00.000Z""}},""force"":true}]}}";

            using var growthBook = new GrowthBook(new Context
            {
                Attributes = JObject.FromObject(new { d = "2026-10-20T00:00:00.000Z" }),
                EncryptedFeatures = Encrypt(payload),
                DecryptionKey = DecryptionKey,
                LoggerFactory = NullLoggerFactory.Instance
            });

            growthBook.Features["flag"].Rules[0].Condition["d"]["$lt"].Type.Should().Be(JTokenType.String);
            growthBook.IsOn("flag").Should().BeFalse("because the attribute equals the strict cutoff");
        }
    }
}
