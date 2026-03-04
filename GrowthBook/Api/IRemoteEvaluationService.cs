using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook.Api
{
    /// <summary>
    /// Service interface for performing remote evaluation of features and experiments.
    /// Remote evaluation sends user attributes to the server for evaluation instead of
    /// evaluating features locally, providing enhanced security and reduced client-side overhead.
    /// </summary>
    public interface IRemoteEvaluationService
    {
        /// <summary>
        /// Performs remote evaluation by sending user attributes and context to the server.
        /// The server evaluates all features and returns only those that are relevant to the user.
        /// </summary>
        /// <param name="apiHost">The GrowthBook API host URL</param>
        /// <param name="clientKey">The SDK client key for authentication</param>
        /// <param name="request">The evaluation request containing user attributes and context</param>
        /// <param name="headers">Optional additional HTTP headers to include in the request</param>
        /// <param name="cancellationToken">Cancellation token for the async operation</param>
        /// <returns>A response containing evaluated features specific to the user</returns>
        /// <exception cref="System.ArgumentNullException">Thrown when required parameters are null</exception>
        /// <exception cref="System.ArgumentException">Thrown when parameters are invalid</exception>
        /// <exception cref="RemoteEvaluationException">Thrown when the remote evaluation fails</exception>
        Task<RemoteEvaluationResponse> EvaluateAsync(
            string? apiHost,
            string? clientKey,
            RemoteEvaluationRequest request,
            IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Generates the remote evaluation endpoint URL for the given API host and client key.
        /// </summary>
        /// <param name="apiHost">The GrowthBook API host URL</param>
        /// <param name="clientKey">The SDK client key</param>
        /// <returns>The complete URL for the remote evaluation endpoint</returns>
        /// <exception cref="System.ArgumentException">Thrown when parameters are null or empty</exception>
        string GetRemoteEvaluationUrl(string apiHost, string clientKey);

        /// <summary>
        /// Validates that the provided configuration is compatible with remote evaluation.
        /// </summary>
        /// <param name="context">The GrowthBook context to validate</param>
        /// <exception cref="System.ArgumentException">Thrown when the configuration is invalid for remote evaluation</exception>
        void ValidateRemoteEvaluationConfiguration(Context context);
    }
}
