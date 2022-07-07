using GrowthBook;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Growthbook.Tests {
    [TestClass]
    public class GrowthBookTests {
        public static JObject testCases;
        public static JObject customCases;

        [ClassInitialize]
        public static void TestFixtureSetup(TestContext context) {
            testCases = JObject.Parse(File.ReadAllText("../../standard-cases.json"));
            customCases = JObject.Parse(File.ReadAllText("../../custom-cases.json"));
        }

        public static string GetTestNames(MethodInfo methodInfo, object[] values) {
            return $"{methodInfo.Name} - { values[0] }";
        }

        [TestMethod]
        [DynamicData(nameof(RunTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void Run(string testname, Context context, Experiment experiment, JToken expectedValue, bool inExperiment, bool hashUsed) {
            GrowthBook.GrowthBook gb = new GrowthBook.GrowthBook(context);
            ExperimentResult actual = gb.Run(experiment);
            Assert.AreEqual(inExperiment, actual.InExperiment);
            Assert.AreEqual(hashUsed, actual.HashUsed);
            Assert.IsTrue(JToken.DeepEquals(actual.Value, expectedValue));
        }

        public static IEnumerable<object[]> RunTests() {
            foreach (JArray testCase in (JArray)testCases["run"]) {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToObject<Context>(),
                    testCase[2].ToObject<Experiment>(),
                    testCase[3],
                    testCase[4].ToObject<bool>(),
                    testCase[5].ToObject<bool>(),
                };
            }
        }

        [TestMethod]
        public void Run_ShouldCallTrackingCallbackOnce() {
            JArray testCase = (JArray)customCases["run"];
            int trackingCounter = 0;

            Context context = testCase[0].ToObject<Context>();
            context.TrackingCallback = (Experiment experiment, ExperimentResult result) => {
                Assert.IsTrue(JToken.DeepEquals(result.Value, testCase[2]));
                Assert.AreEqual(testCase[3].ToObject<bool>(), result.InExperiment);
                Assert.AreEqual(testCase[4].ToObject<bool>(), result.HashUsed);
                trackingCounter++;
            };

            GrowthBook.GrowthBook gb = new GrowthBook.GrowthBook(context);
            gb.Run(testCase[1].ToObject<Experiment>());
            gb.Run(testCase[1].ToObject<Experiment>());
            Assert.AreEqual(1, trackingCounter);
        }

        [TestMethod]
        public void Run_ShouldCallSubscribedCallbacks() {
            JArray testCase = (JArray)customCases["run"];
            GrowthBook.GrowthBook gb = new GrowthBook.GrowthBook(testCase[0].ToObject<Context>());

            int subCounterOne = 0;
            gb.Subscribe((Experiment experiment, ExperimentResult result) => {
                Assert.IsTrue(JToken.DeepEquals(result.Value, testCase[2]));
                Assert.AreEqual(testCase[3].ToObject<bool>(), result.InExperiment);
                Assert.AreEqual(testCase[4].ToObject<bool>(), result.HashUsed);
                subCounterOne++;
            });

            int subCounterTwo = 0;
            Action unsubscribe = gb.Subscribe((Experiment experiment, ExperimentResult result) => {
                Assert.IsTrue(JToken.DeepEquals(result.Value, testCase[2]));
                Assert.AreEqual(testCase[3].ToObject<bool>(), result.InExperiment);
                Assert.AreEqual(testCase[4].ToObject<bool>(), result.HashUsed);
                subCounterTwo++;
            });
            unsubscribe();

            int subCounterThree = 0;
            gb.Subscribe((Experiment experiment, ExperimentResult result) => {
                Assert.IsTrue(JToken.DeepEquals(result.Value, testCase[2]));
                Assert.AreEqual(testCase[3].ToObject<bool>(), result.InExperiment);
                Assert.AreEqual(testCase[4].ToObject<bool>(), result.HashUsed);
                subCounterThree++;
            });

            gb.Run(testCase[1].ToObject<Experiment>());
            gb.Run(testCase[1].ToObject<Experiment>());
            gb.Run(testCase[1].ToObject<Experiment>());
            Assert.AreEqual(1, subCounterOne);
            Assert.AreEqual(0, subCounterTwo);
            Assert.AreEqual(1, subCounterThree);
        }

        [TestMethod]
        [DynamicData(nameof(EvalFeatureTests), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetTestNames))]
        public void EvalFeature(string testname, Context context, string key, FeatureResult expected) {
            GrowthBook.GrowthBook gb = new GrowthBook.GrowthBook(context);
            FeatureResult actual = gb.EvalFeature(key);
            Assert.AreEqual(expected, actual);
        }

        public static IEnumerable<object[]> EvalFeatureTests() {
            foreach (JArray testCase in (JArray)testCases["feature"]) {
                yield return new object[] {
                    testCase[0].ToString(),
                    testCase[1].ToObject<Context>(),
                    testCase[2].ToString(),
                    testCase[3].ToObject<FeatureResult>(),
                };
            }
        }
    }
}
