using System;
using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// Result of a feature loading operation.
    /// </summary>
    public class FeatureLoadResult
    {
        /// <summary>
        /// Whether the feature loading operation was successful.
        /// </summary>
        public bool Success { get; private set; }

        /// <summary>
        /// Number of features that were loaded, if successful.
        /// </summary>
        public int FeatureCount { get; private set; }

        /// <summary>
        /// Error message if the operation failed.
        /// </summary>
        public string? ErrorMessage { get; private set; }

        /// <summary>
        /// HTTP status code if the failure was due to an HTTP error.
        /// </summary>
        public int? StatusCode { get; private set; }

        /// <summary>
        /// The original exception that caused the failure, if any.
        /// </summary>
        public Exception? Exception { get; private set; }

        private FeatureLoadResult() { }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static FeatureLoadResult CreateSuccess(int featureCount)
        {
            return new FeatureLoadResult
            {
                Success = true,
                FeatureCount = featureCount
            };
        }

        /// <summary>
        /// Creates a failed result.
        /// </summary>
        public static FeatureLoadResult CreateFailure(string errorMessage, Exception? exception = null, int? statusCode = null)
        {
            return new FeatureLoadResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                Exception = exception,
                StatusCode = statusCode
            };
        }

        public override string ToString()
        {
            if (Success)
            {
                return $"Success: Loaded {FeatureCount} features";
            }
            
            var result = $"Failed: {ErrorMessage}";
            if (StatusCode.HasValue)
            {
                result += $" (HTTP {StatusCode})";
            }
            return result;
        }
    }
}