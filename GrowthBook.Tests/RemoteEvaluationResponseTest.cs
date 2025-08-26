using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using GrowthBook;
using Xunit;

namespace GrowthBook.Tests
{
    public class RemoteEvaluationResponseTests
    {
        [Fact]
        public void CreateSuccess_ShouldWork()
        {
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };
            var response = RemoteEvaluationResponse.CreateSuccess(features);

            response.IsSuccess.Should().BeTrue();
            response.Features.Should().HaveCount(1);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public void CreateError_ShouldWork()
        {
            var response = RemoteEvaluationResponse.CreateError(HttpStatusCode.BadRequest, "Error");

            response.IsSuccess.Should().BeFalse();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.ErrorMessage.Should().Be("Error");
        }

        [Fact]
        public void IsSuccess_ShouldReturnCorrectValue()
        {
            var successResponse = new RemoteEvaluationResponse
            {
                StatusCode = HttpStatusCode.OK,
                Features = new Dictionary<string, Feature>()
            };
            successResponse.IsSuccess.Should().BeTrue();

            var errorResponse = new RemoteEvaluationResponse
            {
                StatusCode = HttpStatusCode.BadRequest,
                Features = new Dictionary<string, Feature>()
            };
            errorResponse.IsSuccess.Should().BeFalse();
        }
    }
}
