using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using GrowthBook.Tests.Json;

using Newtonsoft.Json.Linq;

using Xunit;

namespace GrowthBook.Tests;

public class GrowthBookTests
{

    #region helpers
    public static JObject getStandardCases()
    {
        return JObject.Parse(JsonTestHelpers.GetTestJson("standard-cases"));
    }

    public static JObject getCustomCases()
    {
        return JObject.Parse(JsonTestHelpers.GetTestJson("custom-cases"));
    }

    public static string GetTestNames(MethodInfo methodInfo, object[] values)
    {
        return $"{methodInfo.Name} - {values[0]}";
    }
    #endregion

    #region data
    public static IEnumerable<object[]> RunTests()
    {
        foreach (JArray testCase in (JArray)getStandardCases()["run"])
        {
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
    public static IEnumerable<object[]> EvalFeatureTests()
    {
        foreach (JArray testCase in (JArray)getStandardCases()["feature"])
        {
            yield return new object[] {
                testCase[0].ToString(),
                testCase[1].ToObject<Context>(),
                testCase[2].ToString(),
                testCase[3].ToObject<FeatureResult>(),
            };
        }
    }
    #endregion

    [Fact]
    public void Run_ShouldCallTrackingCallbackOnce()
    {
        JArray testCase = (JArray)getCustomCases()["run"];
        int trackingCounter = 0;

        Context context = testCase[0].ToObject<Context>();
        context.TrackingCallback = (Experiment experiment, ExperimentResult result) =>
        {
            Assert.True(JToken.DeepEquals(result.Value, testCase[2]));
            Assert.Equal(testCase[3].ToObject<bool>(), result.InExperiment);
            Assert.Equal(testCase[4].ToObject<bool>(), result.HashUsed);
            trackingCounter++;
        };

        GrowthBook gb = new(context);
        gb.Run(testCase[1].ToObject<Experiment>());
        gb.Run(testCase[1].ToObject<Experiment>());
        Assert.Equal(1, trackingCounter);
    }

    [Fact]
    public void Run_ShouldCallSubscribedCallbacks()
    {
        JArray testCase = (JArray)getCustomCases()["run"];
        GrowthBook gb = new(testCase[0].ToObject<Context>());

        int subCounterOne = 0;
        gb.Subscribe((Experiment experiment, ExperimentResult result) =>
        {
            Assert.True(JToken.DeepEquals(result.Value, testCase[2]));
            Assert.Equal(testCase[3].ToObject<bool>(), result.InExperiment);
            Assert.Equal(testCase[4].ToObject<bool>(), result.HashUsed);
            subCounterOne++;
        });

        int subCounterTwo = 0;
        Action unsubscribe = gb.Subscribe((Experiment experiment, ExperimentResult result) =>
        {
            Assert.True(JToken.DeepEquals(result.Value, testCase[2]));
            Assert.Equal(testCase[3].ToObject<bool>(), result.InExperiment);
            Assert.Equal(testCase[4].ToObject<bool>(), result.HashUsed);
            subCounterTwo++;
        });
        unsubscribe();

        int subCounterThree = 0;
        gb.Subscribe((Experiment experiment, ExperimentResult result) =>
        {
            Assert.True(JToken.DeepEquals(result.Value, testCase[2]));
            Assert.Equal(testCase[3].ToObject<bool>(), result.InExperiment);
            Assert.Equal(testCase[4].ToObject<bool>(), result.HashUsed);
            subCounterThree++;
        });

        gb.Run(testCase[1].ToObject<Experiment>());
        gb.Run(testCase[1].ToObject<Experiment>());
        gb.Run(testCase[1].ToObject<Experiment>());
        Assert.Equal(1, subCounterOne);
        Assert.Equal(0, subCounterTwo);
        Assert.Equal(1, subCounterThree);
    }

    [Theory]
    [MemberData(nameof(RunTests))]
    public void Run(string testName, Context context, Experiment experiment, JToken expectedValue, bool inExperiment, bool hashUsed)
    {
        if (testName is null)
        {
            throw new ArgumentNullException(nameof(testName));
        }

        GrowthBook gb = new(context);
        ExperimentResult actual = gb.Run(experiment);
        Assert.Equal(inExperiment, actual.InExperiment);
        Assert.Equal(hashUsed, actual.HashUsed);
        Assert.True(JToken.DeepEquals(actual.Value, expectedValue));
    }

    [Theory]
    [MemberData(nameof(EvalFeatureTests))]
    public void EvalFeature(string testname, Context context, string key, FeatureResult expected)
    {
        if (testname is null)
        {
            throw new ArgumentNullException(nameof(testname));
        }

        GrowthBook gb = new(context);
        FeatureResult actual = gb.EvalFeature(key);
        Assert.Equal(expected, actual);
    }

}
