using System;

namespace GrowthBook.Exceptions
{
    /// <summary>
    /// Base exception for GrowthBook SDK errors.
    /// </summary>
    public class GrowthBookException : Exception
    {
        public GrowthBookException(string message) : base(message)
        {
        }

        public GrowthBookException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Exception thrown when features cannot be loaded from the API.
    /// </summary>
    public class FeatureLoadException : GrowthBookException
    {
        /// <summary>
        /// HTTP status code returned from the API, if applicable.
        /// </summary>
        public int? StatusCode { get; }

        public FeatureLoadException(string message) : base(message)
        {
        }

        public FeatureLoadException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        public FeatureLoadException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public FeatureLoadException(string message, int statusCode, Exception innerException) : base(message, innerException)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Exception thrown when remote evaluation fails.
    /// </summary>
    public class RemoteEvaluationException : GrowthBookException
    {
        /// <summary>
        /// HTTP status code returned from the remote evaluation API, if applicable.
        /// </summary>
        public int? StatusCode { get; }

        /// <summary>
        /// Response body from the failed request, if available.
        /// </summary>
        public string? ResponseBody { get; }

        public RemoteEvaluationException(string message) : base(message)
        {
        }

        public RemoteEvaluationException(string message, int? statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        public RemoteEvaluationException(string message, int? statusCode, Exception innerException) : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        public RemoteEvaluationException(string message, int? statusCode, string responseBody) : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        public RemoteEvaluationException(string message, int? statusCode, string responseBody, Exception innerException) : base(message, innerException)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}