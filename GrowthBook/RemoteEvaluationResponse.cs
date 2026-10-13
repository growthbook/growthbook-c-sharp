using System;
using System.Collections.Generic;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents the response from a remote evaluation API call.
    /// Contains the evaluated features specific to the user's attributes.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class RemoteEvaluationResponse
    {
        /// <summary>
        /// Dictionary of feature definitions that have been evaluated and filtered
        /// for the specific user. Only contains features that are relevant to the user.
        /// </summary>
        [JsonProperty("features")]
        public IDictionary<string, Feature> Features { get; set; } = new Dictionary<string, Feature>();

        /// <summary>
        /// Contextual bandit definitions the evaluation was made against, keyed by the reference a feature rule
        /// points at. Present only when the payload carried any.
        /// </summary>
        [JsonProperty("contextualBandits")]
        public IDictionary<string, ContextualBanditDefinition> ContextualBandits { get; set; }

        /// <summary>
        /// Timestamp indicating when the features were last updated.
        /// Used for cache invalidation and freshness checks.
        /// </summary>
        [JsonProperty("dateUpdated")]
        public DateTimeOffset? DateUpdated { get; set; }

        /// <summary>
        /// HTTP status code of the response.
        /// Used for error handling and debugging.
        /// </summary>
        [JsonIgnore]
        public HttpStatusCode StatusCode { get; set; }

        /// <summary>
        /// Indicates whether the remote evaluation was successful.
        /// </summary>
        [JsonIgnore]
        public bool IsSuccess => StatusCode == HttpStatusCode.OK && Features != null;

        /// <summary>
        /// Error message if the remote evaluation failed.
        /// </summary>
        [JsonIgnore]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a new RemoteEvaluationResponse with default values.
        /// </summary>
        public RemoteEvaluationResponse()
        {
        }

        /// <summary>
        /// Creates a successful RemoteEvaluationResponse with the provided features.
        /// </summary>
        /// <param name="features">The evaluated features</param>
        /// <param name="dateUpdated">When the features were last updated</param>
        /// <returns>A successful response</returns>
        /// <remarks>
        /// Kept as its own overload rather than gaining an optional parameter. Adding one changes the
        /// signature callers were compiled against, so an assembly built against an earlier version would
        /// fail with <see cref="MissingMethodException"/> at the call rather than at build time.
        /// </remarks>
        public static RemoteEvaluationResponse CreateSuccess(
            IDictionary<string, Feature> features,
            DateTimeOffset? dateUpdated = null)
        {
            return CreateSuccess(features, dateUpdated, null);
        }

        /// <summary>
        /// Creates a successful RemoteEvaluationResponse with the provided features and the contextual
        /// bandit definitions they were evaluated against.
        /// </summary>
        /// <param name="features">The evaluated features</param>
        /// <param name="dateUpdated">When the features were last updated</param>
        /// <param name="contextualBandits">The bandit definitions the payload carried, if any</param>
        /// <returns>A successful response</returns>
        public static RemoteEvaluationResponse CreateSuccess(
            IDictionary<string, Feature> features,
            DateTimeOffset? dateUpdated,
            IDictionary<string, ContextualBanditDefinition> contextualBandits)
        {
            return new RemoteEvaluationResponse
            {
                Features = features ?? new Dictionary<string, Feature>(),
                DateUpdated = dateUpdated ?? DateTimeOffset.UtcNow,
                ContextualBandits = contextualBandits,
                StatusCode = HttpStatusCode.OK
            };
        }

        /// <summary>
        /// Creates a failed RemoteEvaluationResponse with error information.
        /// </summary>
        /// <param name="statusCode">HTTP status code of the failed request</param>
        /// <param name="errorMessage">Error message describing the failure</param>
        /// <returns>A failed response</returns>
        public static RemoteEvaluationResponse CreateError(HttpStatusCode statusCode, string errorMessage)
        {
            return new RemoteEvaluationResponse
            {
                StatusCode = statusCode,
                ErrorMessage = errorMessage,
                Features = new Dictionary<string, Feature>()
            };
        }

        /// <summary>
        /// Creates a failed RemoteEvaluationResponse from an exception.
        /// </summary>
        /// <param name="exception">The exception that caused the failure</param>
        /// <returns>A failed response</returns>
        public static RemoteEvaluationResponse CreateError(Exception exception)
        {
            return new RemoteEvaluationResponse
            {
                StatusCode = HttpStatusCode.InternalServerError,
                ErrorMessage = exception?.Message ?? "Unknown error occurred during remote evaluation",
                Features = new Dictionary<string, Feature>()
            };
        }
    }
}
