using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Exceptions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace GrowthBook.Api
{
    /// <summary>
    /// Service for performing remote evaluation of features and experiments.
    /// Sends user attributes to the server and receives pre-evaluated features.
    /// </summary>
    public class RemoteEvaluationService : IRemoteEvaluationService
    {
        private readonly ILogger<RemoteEvaluationService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        /// <summary>
        /// Creates a new RemoteEvaluationService instance.
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <param name="httpClientFactory">Factory for creating HTTP clients</param>
        public RemoteEvaluationService(
            ILogger<RemoteEvaluationService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        /// <inheritdoc />
        public async Task<RemoteEvaluationResponse> EvaluateAsync(
            string apiHost,
            string clientKey,
            RemoteEvaluationRequest request,
            IDictionary<string, string> headers = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiHost))
                throw new ArgumentException("API host cannot be null or empty", nameof(apiHost));

            if (string.IsNullOrWhiteSpace(clientKey))
                throw new ArgumentException("Client key cannot be null or empty", nameof(clientKey));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var url = GetRemoteEvaluationUrl(apiHost, clientKey);

            _logger.LogInformation("Starting remote evaluation request to {Url}", url);
            _logger.LogDebug("Remote evaluation request payload: {Payload}", JsonConvert.SerializeObject(request));

            try
            {
                using (var httpClient = _httpClientFactory.CreateClient())
                {
                    // Set default timeout if not configured
                    if (httpClient.Timeout == Timeout.InfiniteTimeSpan)
                    {
                        httpClient.Timeout = TimeSpan.FromSeconds(30);
                    }

                    // Prepare the request
                    using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        // Add custom headers
                        if (headers != null)
                        {
                            foreach (var header in headers)
                            {
                                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                            }
                        }

                        // Set content type
                        var jsonPayload = JsonConvert.SerializeObject(request);
                        httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                        _logger.LogDebug("Sending POST request to remote evaluation endpoint");

                        // Make the request
                        using (var response = await httpClient.SendAsync(httpRequest, cancellationToken))
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();

                            _logger.LogDebug("Received response with status {StatusCode}: {Response}",
                                response.StatusCode, responseContent);

                            if (response.IsSuccessStatusCode)
                            {
                                var featuresResponse = JsonConvert.DeserializeObject<Dictionary<string, Feature>>(responseContent);

                                _logger.LogInformation("Remote evaluation successful, received {Count} features",
                                    featuresResponse?.Count ?? 0);

                                return RemoteEvaluationResponse.CreateSuccess(featuresResponse);
                            }
                            else
                            {
                                var errorMessage = $"Remote evaluation failed with status {response.StatusCode}: {responseContent}";
                                _logger.LogError(errorMessage);

                                return RemoteEvaluationResponse.CreateError(response.StatusCode, errorMessage);
                            }
                        }
                    }
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                var errorMessage = "Remote evaluation request timed out";
                _logger.LogError(ex, errorMessage);
                throw new RemoteEvaluationException(errorMessage, (int)HttpStatusCode.RequestTimeout, ex);
            }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                var errorMessage = "Remote evaluation request was cancelled";
                _logger.LogWarning(ex, errorMessage);
                throw new OperationCanceledException(errorMessage, ex, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var errorMessage = $"HTTP error during remote evaluation: {ex.Message}";
                _logger.LogError(ex, errorMessage);
                throw new RemoteEvaluationException(errorMessage, null, ex);
            }
            catch (JsonException ex)
            {
                var errorMessage = $"Failed to parse remote evaluation response: {ex.Message}";
                _logger.LogError(ex, errorMessage);
                throw new RemoteEvaluationException(errorMessage, 422, ex); // 422 Unprocessable Entity
            }
            catch (Exception ex)
            {
                var errorMessage = $"Unexpected error during remote evaluation: {ex.Message}";
                _logger.LogError(ex, errorMessage);
                throw new RemoteEvaluationException(errorMessage, null, ex);
            }
        }

        /// <inheritdoc />
        public string GetRemoteEvaluationUrl(string apiHost, string clientKey)
        {
            if (string.IsNullOrWhiteSpace(apiHost))
                throw new ArgumentException("API host cannot be null or empty", nameof(apiHost));

            if (string.IsNullOrWhiteSpace(clientKey))
                throw new ArgumentException("Client key cannot be null or empty", nameof(clientKey));

            // Remove trailing slashes from API host
            var trimmedHost = apiHost.TrimEnd('/');

            // Build the remote evaluation endpoint URL
            var url = $"{trimmedHost}/api/eval/{clientKey}";

            _logger.LogDebug("Generated remote evaluation URL: {Url}", url);

            return url;
        }

        /// <inheritdoc />
        public void ValidateRemoteEvaluationConfiguration(Context context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (!context.RemoteEval)
                return; // No validation needed if remote eval is disabled

            if (string.IsNullOrWhiteSpace(context.ClientKey))
            {
                throw new ArgumentException(
                    "ClientKey is required when RemoteEval is enabled",
                    nameof(context));
            }

            if (!string.IsNullOrWhiteSpace(context.DecryptionKey))
            {
                throw new ArgumentException(
                    "RemoteEval cannot be used with DecryptionKey. " +
                    "Remote evaluation requires the server to have access to unencrypted features for evaluation.",
                    nameof(context));
            }

            if (string.IsNullOrWhiteSpace(context.ApiHost))
            {
                throw new ArgumentException(
                    "ApiHost is required when RemoteEval is enabled",
                    nameof(context));
            }

            _logger.LogDebug("Remote evaluation configuration validation passed");
        }
    }
}
