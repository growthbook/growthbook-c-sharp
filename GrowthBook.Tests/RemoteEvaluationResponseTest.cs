using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
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

        /// <summary>
        /// A call compiled against a method binds to its exact signature, so turning the two-parameter overload
        /// into a three-parameter one with a default does not keep existing assemblies working - they resolve a
        /// method that is no longer there and fail at the call site rather than at build time. Asserted by
        /// reflection because that is the only thing that sees the signature rather than the source.
        /// </summary>
        [Fact]
        public void CreateSuccess_ShouldKeepTheOverloadCompiledCallersBindTo()
        {
            var overload = typeof(RemoteEvaluationResponse).GetMethod(
                nameof(RemoteEvaluationResponse.CreateSuccess),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(IDictionary<string, Feature>), typeof(DateTimeOffset?) },
                modifiers: null);

            overload.Should().NotBeNull(
                "an assembly built against the earlier SDK calls CreateSuccess(IDictionary, DateTimeOffset?) and would throw MissingMethodException without it");
        }

        [Fact]
        public void CreateSuccess_WithoutBandits_ShouldBehaveAsItDidBefore()
        {
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };
            var updated = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            var response = RemoteEvaluationResponse.CreateSuccess(features, updated);

            response.IsSuccess.Should().BeTrue();
            response.DateUpdated.Should().Be(updated);
            response.ContextualBandits.Should().BeNull("a caller that knows nothing about bandits must not acquire any");
        }

        [Fact]
        public void CreateSuccess_WithBandits_ShouldCarryThem()
        {
            var features = new Dictionary<string, Feature> { { "test", new Feature { DefaultValue = true } } };
            var bandits = new Dictionary<string, ContextualBanditDefinition>
            {
                { "cb", new ContextualBanditDefinition { BanditVersion = 3 } }
            };

            var response = RemoteEvaluationResponse.CreateSuccess(features, null, bandits);

            response.ContextualBandits.Should().ContainKey("cb");
            response.ContextualBandits["cb"].BanditVersion.Should().Be(3);
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
