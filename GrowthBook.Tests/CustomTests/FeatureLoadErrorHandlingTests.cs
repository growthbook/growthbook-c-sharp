using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GrowthBookSdk = GrowthBook;

namespace GrowthBook.Tests.CustomTests
{
    /// <summary>
    /// Tests for error handling improvements - GitHub issue #43
    /// </summary>
    public class FeatureLoadErrorHandlingTests
    {
        [Fact]
        public void IsOn_Should_Not_Crash_With_Empty_Features()
        {
            // Test the main issue from GitHub #43 - NullReferenceException prevention
            var context = new Context
            {
                Enabled = true,
                Features = new Dictionary<string, Feature>(), // Empty but not null
                LoggerFactory = NullLoggerFactory.Instance
            };

            using var growthBookInstance = new GrowthBookSdk.GrowthBook(context);

            // This should not throw NullReferenceException
            var act = () => growthBookInstance.IsOn("NonExistentFeature");
            
            act.Should().NotThrow<NullReferenceException>();
            
            // Should return false for non-existent features
            var result = growthBookInstance.IsOn("NonExistentFeature");
            result.Should().BeFalse();
        }

        [Fact]
        public void FeatureLoadResult_Should_Provide_Meaningful_Success_Info()
        {
            // Test FeatureLoadResult success case
            var successResult = FeatureLoadResult.CreateSuccess(3);
            
            successResult.Success.Should().BeTrue();
            successResult.FeatureCount.Should().Be(3);
            successResult.ErrorMessage.Should().BeNull();
            successResult.StatusCode.Should().BeNull();
            successResult.Exception.Should().BeNull();
        }

        [Fact]
        public void FeatureLoadResult_Should_Provide_Meaningful_Failure_Info()
        {
            // Test FeatureLoadResult failure case
            var failureResult = FeatureLoadResult.CreateFailure("Test error message", null, 400);
            
            failureResult.Success.Should().BeFalse();
            failureResult.FeatureCount.Should().Be(0);
            failureResult.ErrorMessage.Should().Be("Test error message");
            failureResult.StatusCode.Should().Be(400);
            failureResult.Exception.Should().BeNull();
        }
    }
}