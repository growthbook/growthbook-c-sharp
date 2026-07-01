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
        public string ErrorMessage { get; private set; }

        /// <summary>
        /// HTTP status code if the failure was due to an HTTP error.
        /// </summary>
        public int? StatusCode { get; private set; }

        /// <summary>
        /// The original exception that caused the failure, if any.
        /// </summary>
        public Exception Exception { get; private set; }

        /// <summary>
        ///  Whether the server returns modified features (HHTP 200) as opposed to confirming the
        /// cached features are still valid (HHTP 304 Not Modified).
        /// <para>
        /// Reliable only when the load waited for the server round-trip - i.e the first load
        /// (empty cache or when <sse cref="GrowthBookRetrievalOptions.WaitForCompletion"/> is true.
        /// In the fire-and-forget path the method returns before the refresh copletes, and for remote evaluation there is no 304 concept, so this stays <c>false</c> in those cases.
        /// </para>
        /// </summary>
        public bool WasModified { get; private set; }

        private FeatureLoadResult() { }

        /// <summary>
        /// Creates a successful result.
        /// </summary>
        public static FeatureLoadResult CreateSuccess(int featureCount, bool wasModified = true)
        {
            return new FeatureLoadResult
            {
                Success = true,
                FeatureCount = featureCount,
                WasModified = wasModified
            };
        }

        /// <summary>
        /// Creates a failed result.
        /// </summary>
        public static FeatureLoadResult CreateFailure(string errorMessage, Exception exception = null, int? statusCode = null)
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
