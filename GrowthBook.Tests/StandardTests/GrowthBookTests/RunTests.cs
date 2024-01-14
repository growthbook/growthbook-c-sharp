using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollector.InProcDataCollector;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrowthBook.Tests.StandardTests.GrowthBookTests;

public class RunTests : UnitTest
{   
    public class RunTestCase
    {
        [TestPropertyIndex(0)]
        public string TestName { get; set; }
        [TestPropertyIndex(1)]
        public Context Context { get; set; }
        [TestPropertyIndex(2)]
        public Experiment Experiment { get; set; }
        [TestPropertyIndex(3)]
        public JToken ExpectedValue { get; set; }
        [TestPropertyIndex(4)]
        public bool InExperiment { get; set; }
        [TestPropertyIndex(5)]
        public bool HashUsed { get; set; }
    }

    [StandardCaseTestCategory("run")]
    public class RunStandardTestCase : RunTestCase { }

    [CustomCaseTestCategory("run")]
    public class RunCustomTestCase : RunTestCase { }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(RunStandardTestCase))]
    public void Run(RunStandardTestCase testCase)
    {
        var gb = new GrowthBook(testCase.Context);
        var actualResult = gb.Run(testCase.Experiment);

        actualResult.InExperiment.Should().Be(testCase.InExperiment, "because the logic placing an experiment into buckets should be correct");
        actualResult.HashUsed.Should().Be(testCase.HashUsed, "because the logic determining which hash to use should be correct");
        actualResult.Value.Should().BeEquivalentTo(testCase.ExpectedValue, "because the end result value should reflect the correct experiment decisioning");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(RunCustomTestCase))]
    public void RunCustomShouldCallTrackingCallbackOnce(RunCustomTestCase testCase)
    {
        var trackingCounter = 0;

        testCase.Context.TrackingCallback = (experiment, actualResult) =>
        {
            actualResult.InExperiment.Should().Be(testCase.InExperiment, "because the logic placing an experiment into buckets should be correct");
            actualResult.HashUsed.Should().Be(testCase.HashUsed, "because the logic determining which hash to use should be correct");
            actualResult.Value.Should().BeEquivalentTo(testCase.ExpectedValue, "because the end result value should reflect the correct experiment decisioning");

            trackingCounter++;
        };

        GrowthBook gb = new(testCase.Context);

        gb.Run(testCase.Experiment);
        gb.Run(testCase.Experiment);

        trackingCounter.Should().Be(1, "because the callback should only be invoked once unless the result changes");
    }

    [Theory]
    [MemberData(nameof(GetMappedTestsInCategory), typeof(RunCustomTestCase))]
    public void RunCustomShouldCallSubscribedCallbacks(RunCustomTestCase testCase)
    {
        void ExperimentResultShouldMeetExpectations(ExperimentResult actualResult)
        {
            actualResult.InExperiment.Should().Be(testCase.InExperiment, "because the logic placing an experiment into buckets should be correct");
            actualResult.HashUsed.Should().Be(testCase.HashUsed, "because the logic determining which hash to use should be correct");
            actualResult.Value.Should().BeEquivalentTo(testCase.ExpectedValue, "because the end result value should reflect the correct experiment decisioning");
        }

        GrowthBook gb = new(testCase.Context);

        var subCounterOne = 0;

        gb.Subscribe((experiment, actualResult) =>
        {
            ExperimentResultShouldMeetExpectations(actualResult);

            subCounterOne++;
        });

        var subCounterTwo = 0;

        Action unsubscribe = gb.Subscribe((experiment, actualResult) =>
        {
            ExperimentResultShouldMeetExpectations(actualResult);

            subCounterTwo++;
        });

        unsubscribe();

        var subCounterThree = 0;

        gb.Subscribe((experiment, actualResult) =>
        {
            ExperimentResultShouldMeetExpectations(actualResult);

            subCounterThree++;
        });

        gb.Run(testCase.Experiment);
        gb.Run(testCase.Experiment);
        gb.Run(testCase.Experiment);

        subCounterOne.Should().Be(1, "because the subscription callback should only be invoked once unless the result changes");
        subCounterTwo.Should().Be(0, "because the subscription callback was registered and immediately unregistered");
        subCounterThree.Should().Be(1, "because the subscription callback should only be invoked once unless the result changes");
    }
}
