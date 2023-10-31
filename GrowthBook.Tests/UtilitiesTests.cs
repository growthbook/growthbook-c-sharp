using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using GrowthBook.Tests.Json;

using Newtonsoft.Json.Linq;

using Xunit;

namespace GrowthBook.Tests
{
    public class UtilitiesTests
    {
        #region helpers

        public static JObject getStandardCases()
        {
            return JObject.Parse(JsonTestHelpers.GetTestJson("standard-cases"));
        }

        public static string GetTestNames(MethodInfo methodInfo, object[] values)
        {
            return $"{methodInfo.Name} - {values[0]}";
        }

        public static double RoundStandard(double input) => Math.Round(input, 6);

        public static IList<double> RoundArray(IList<double> input)
        {
            var results = new List<double>();
            for (int i = 0; i < input.Count; i++)
            {
                results.Add(RoundStandard(input[i]));
            }
            return results;
        }

        public IList<BucketRange> RoundBucketRanges(IList<BucketRange> input)
        {
            var results = new List<BucketRange>();
            foreach (BucketRange range in input)
            {
                results.Add(new BucketRange(RoundStandard(range.Start), RoundStandard(range.End)));
            }
            return results;
        }
        #endregion
        #region data
        public static IEnumerable<object[]> GetBucketRangeTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["getBucketRange"]).Cast<JArray>())
            {
                var expected = new List<BucketRange>();
                foreach (JArray jArray in testCase[2].Cast<JArray>())
                {
                    expected.Add(new BucketRange(jArray[0].ToObject<double>(), jArray[1].ToObject<double>()));
                }
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1][0].ToObject<int>(),
                    testCase[1][1].ToObject<double>(),
                    testCase[1][2].ToObject<double[]>(),
                    expected,
                };
            }
        }

        public static IEnumerable<object[]> ChooseVariationTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["chooseVariation"]).Cast<JArray>())
            {
                var ranges = new List<BucketRange>();
                foreach (JArray jArray in testCase[2].Cast<JArray>())
                {
                    ranges.Add(new BucketRange(jArray[0].ToObject<double>(), jArray[1].ToObject<double>()));
                }
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToObject<double>(),
                    ranges,
                    testCase[3].ToObject<int>(),
                };
            }
        }

        public static IEnumerable<object[]> InNamespaceTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["inNamespace"]).Cast<JArray>())
            {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToString(),
                    testCase[2][0].ToString(),
                    testCase[2][1].ToObject<double>(),
                    testCase[2][2].ToObject<double>(),
                    testCase[3].ToObject<bool>(),
                };
            }
        }

        public static IEnumerable<object[]> GetQueryStringOverrideTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["getQueryStringOverride"]).Cast<JArray>())
            {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToString(),
                    testCase[2].ToString(),
                    testCase[3].ToObject<int>(),
                    testCase[4].ToObject<int?>(),
                };
            }
        }

        public static IEnumerable<object[]> EvalConditionTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["evalCondition"]).Cast<JArray>())
            {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1],
                    testCase[2],
                    testCase[3].ToObject<bool>()
                };
            }
        }

        public static IEnumerable<object[]> GetEqualWeightsTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["getEqualWeights"]).Cast<JArray>())
            {
                yield return new object[] {
                    testCase[0].ToObject<int>(),
                    testCase[1].ToObject<double[]>(),
                };
            }
        }


        public static IEnumerable<object[]> HashTests()
        {
            foreach (JArray testCase in ((JArray)getStandardCases()["hash"]).Cast<JArray>())
            {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToObject<double>()
                };
            }
        }
        #endregion


        [Theory]
        [MemberData(nameof(EvalConditionTests))]
        public void EvalCondition(string testName, JObject condition, JToken attributes, bool expected)
        {
            if (testName is null)
            {
                throw new ArgumentNullException(nameof(testName));
            }

            bool actual = Utilities.EvalCondition(attributes, condition);
            Assert.Equal(expected, actual);
        }


        [Theory]
        [MemberData(nameof(HashTests))]
        public void Hash(string input, double expected)
        {
            double actual = Utilities.Hash(input);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(InNamespaceTests))]
        public void InNamespace(string testName, string userId, string id, double start, double end, bool expected)
        {
            if (testName is null)
            {
                throw new ArgumentNullException(nameof(testName));
            }

            bool actual = Utilities.InNamespace(userId, new Namespace(id, start, end));
            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(GetEqualWeightsTests))]
        public void GetEqualWeights(int input, IList<double> expected)
        {
            IList<double> actual = Utilities.GetEqualWeights(input);
            Assert.True(RoundArray(expected).SequenceEqual(RoundArray(actual)));
        }


        [Theory]
        [MemberData(nameof(GetBucketRangeTests))]
        public void GetBucketRanges(string testName, int numVariations, double coverage, double[] weights, List<BucketRange> expected)
        {
            if (testName is null)
            {
                throw new ArgumentNullException(nameof(testName));
            }

            IList<BucketRange> actual = Utilities.GetBucketRanges(numVariations, coverage, weights);
            Assert.True(RoundBucketRanges(expected).SequenceEqual(RoundBucketRanges(actual)));
        }

        [Theory]
        [MemberData(nameof(ChooseVariationTests))]
        public void ChooseVariation(string testName, double n, List<BucketRange> ranges, int expected)
        {
            if (testName is null)
            {
                throw new ArgumentNullException(nameof(testName));
            }

            int actual = Utilities.ChooseVariation(n, ranges);
            Assert.Equal(expected, actual);
        }

        [Theory]
        [MemberData(nameof(GetQueryStringOverrideTests))]
        public void GetQueryStringOverride(string testName, string id, string url, int numVariations, int? expected)
        {
            if (testName is null)
            {
                throw new ArgumentNullException(nameof(testName));
            }

            int? actual = Utilities.GetQueryStringOverride(id, url, numVariations);
            Assert.Equal(expected, actual);
        }
    }
}
