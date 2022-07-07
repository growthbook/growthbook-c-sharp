using GrowthBook;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Growthbook.Tests {
    [TestClass]
    public class Utilities {
        public static JObject testCases;

        [ClassInitialize]
        public static void TestFixtureSetup(TestContext context) {
            testCases = JObject.Parse(File.ReadAllText("../../standard-cases.json"));
        }

        public static string GetTestNames(MethodInfo methodInfo, object[] values) {
            return $"{methodInfo.Name} - { values[0] }";
        }

        public double RoundStandard(double input) {
            return Math.Round(input, 6);
        }

        public IList<double> RoundArray(IList<double> input) {
            List<double> results = new List<double>();
            for (int i = 0; i < input.Count; i++) {
                results.Add(RoundStandard(input[i]));
            }
            return results;
        }

        public IList<BucketRange> RoundBucketRanges(IList<BucketRange> input) {
            List<BucketRange> results = new List<BucketRange>();
            foreach (BucketRange range in input) {
                results.Add(new BucketRange(RoundStandard(range.Start), RoundStandard(range.End)));
            }
            return results;
        }

        [TestMethod]
        [DynamicData(nameof(HashTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void Hash(string input, double expected) {
            double actual = GrowthBook.Utilities.Hash(input);
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> HashTests() {
            foreach (JArray testCase in (JArray)testCases["hash"]) {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToObject<double>()
                };
            }
        }

        [TestMethod]
        [DynamicData(nameof(InNamespaceTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void InNamespace(string testName, string userId, string id, double start, double end, bool expected) {
            bool actual = GrowthBook.Utilities.InNamespace(userId, new GrowthBook.Namespace(id, start, end));
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> InNamespaceTests() {
            foreach (JArray testCase in (JArray)testCases["inNamespace"]) {
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

        [TestMethod]
        [DynamicData(nameof(GetEqualWeightsTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void GetEqualWeights(int input, IList<double> expected) {
            IList<double> actual = GrowthBook.Utilities.GetEqualWeights(input);
            Assert.IsTrue(RoundArray(expected).SequenceEqual(RoundArray(actual)));
        }

        public static IEnumerable<object[]> GetEqualWeightsTests() {
            foreach (JArray testCase in (JArray)testCases["getEqualWeights"]) {
                yield return new object[] {
                    testCase[0].ToObject<int>(),
                    testCase[1].ToObject<double[]>(),
                };
            }
        }

        [TestMethod]
        [DynamicData(nameof(GetBucketRangeTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void GetBucketRanges(string testName, int numVariations, double coverage, double[] weights, List<BucketRange> expected) {
            IList<BucketRange> actual = GrowthBook.Utilities.GetBucketRanges(numVariations, coverage, weights);
            Assert.IsTrue(RoundBucketRanges(expected).SequenceEqual(RoundBucketRanges(actual)));
        }

        public static IEnumerable<object[]> GetBucketRangeTests() {
            foreach (JArray testCase in (JArray)testCases["getBucketRange"]) {
                List<BucketRange> expected = new List<BucketRange>();
                foreach (JArray jArray in testCase[2]) {
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

        [TestMethod]
        [DynamicData(nameof(ChooseVariationTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void ChooseVariation(string testName, double n, List<BucketRange> ranges, int expected) {
            int actual = GrowthBook.Utilities.ChooseVariation(n, ranges);
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> ChooseVariationTests() {
            foreach (JArray testCase in (JArray)testCases["chooseVariation"]) {
                List<BucketRange> ranges = new List<BucketRange>();
                foreach (JArray jArray in testCase[2]) {
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

        [TestMethod]
        [DynamicData(nameof(GetQueryStringOverrideTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void GetQueryStringOverride(string testName, string id, string url, int numVariations, int? expected) {
            int? actual = GrowthBook.Utilities.GetQueryStringOverride(id, url, numVariations);
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> GetQueryStringOverrideTests() {
            foreach (JArray testCase in (JArray)testCases["getQueryStringOverride"]) {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToString(),
                    testCase[2].ToString(),
                    testCase[3].ToObject<int>(),
                    testCase[4].ToObject<int?>(),
                };
            }
        }

        [TestMethod]
        [DynamicData(nameof(EvalConditionTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void EvalCondition(string testName, JObject condition, JToken attributes, bool expected) {
            bool actual = GrowthBook.Utilities.EvalCondition(attributes, condition);
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> EvalConditionTests() {
            foreach (JArray testCase in (JArray)testCases["evalCondition"]) {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1],
                    testCase[2],
                    testCase[3].ToObject<bool>()
                };
            }
        }
    }
}
