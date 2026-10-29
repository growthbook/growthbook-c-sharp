using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly RemoteEvaluationRetryPolicy _retryPolicy;

        /// <summary>
        /// Creates a new RemoteEvaluationService instance.
        /// </summary>
        /// <param name="logger">Logger for diagnostic information</param>
        /// <param name="httpClientFactory">Factory for creating HTTP clients</param>
        /// <param name="retryPolicy">How to retry a failed request. Uses <see cref="RemoteEvaluationRetryPolicy"/> defaults when omitted.</param>
        public RemoteEvaluationService(
            ILogger<RemoteEvaluationService> logger,
            IHttpClientFactory httpClientFactory,
            RemoteEvaluationRetryPolicy retryPolicy = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _retryPolicy = retryPolicy ?? new RemoteEvaluationRetryPolicy();
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

            // Serialized once, before the first attempt, so every retry sends the payload this round started with.
            // Re-reading the request in between could ship attributes the caller has changed since, and the response
            // would then be applied as though it had been evaluated against them.
            var jsonPayload = JsonConvert.SerializeObject(request);

            _logger.LogInformation("Starting remote evaluation request to {Url}", url);

            // Deliberately not the payload: it is built from the user's attributes, which are personal data, and a
            // debug log ends up in whatever sink the host has configured, with whatever retention it has. The shape of
            // the request is enough to tell whether it was assembled as expected.
            _logger.LogDebug("Remote evaluation payload carries {AttributeCount} attributes, {ForcedFeatureCount} forced features and {ForcedVariationCount} forced variations",
                request.Attributes?.Count ?? 0, request.ForcedFeatures?.Count ?? 0, request.ForcedVariations?.Count ?? 0);

            var maxAttempts = Math.Max(1, _retryPolicy.MaxAttempts);
            var budget = _retryPolicy.MaxTotalDuration > TimeSpan.Zero ? _retryPolicy.MaxTotalDuration : (TimeSpan?)null;
            var elapsed = Stopwatch.StartNew();

            AttemptOutcome outcome = null;

            using (var httpClient = _httpClientFactory.CreateClient(ConfiguredClients.DefaultApiClient))
            {
                // Set default timeout if not configured
                if (httpClient.Timeout == Timeout.InfiniteTimeSpan)
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                }

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    var remaining = budget - elapsed.Elapsed;

                    if (attempt > 1 && remaining.HasValue && remaining.Value <= TimeSpan.Zero)
                    {
                        // The time this round was allowed is gone, so there's nothing left to try with.
                        break;
                    }

                    outcome = await SendAttemptAsync(httpClient, url, jsonPayload, headers, remaining, cancellationToken).ConfigureAwait(false);

                    if (!outcome.IsRetryable)
                    {
                        return outcome.Complete();
                    }

                    if (attempt >= maxAttempts)
                    {
                        break;
                    }

                    var delay = _retryPolicy.GetRetryDelay(attempt, outcome.RetryAfter);
                    var remainingAfterAttempt = budget - elapsed.Elapsed;

                    // If the backoff doesn't fit in what's left of the budget, the attempt it exists to space out
                    // can't happen either. Stop now rather than sleeping out the budget first and failing anyway -
                    // it reports sooner, and it keeps the decision off the boundary where sleeping until the budget
                    // is exactly spent leaves the next check at the mercy of timer precision.
                    if (remainingAfterAttempt.HasValue && delay >= remainingAfterAttempt.Value)
                    {
                        break;
                    }

                    _logger.LogWarning("Remote evaluation attempt {Attempt} of {MaxAttempts} failed ({Reason}), retrying in {DelayMilliseconds}ms",
                        attempt, maxAttempts, outcome.Reason, (long)delay.TotalMilliseconds);

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            // Out of attempts or out of time: report exactly what a single failed request used to report, so callers
            // see no new failure shape - only that the SDK tried harder before giving up.
            _logger.LogError("Remote evaluation failed after {ElapsedMilliseconds}ms: {Reason}",
                (long)elapsed.Elapsed.TotalMilliseconds, outcome.Reason);

            return outcome.Complete();
        }

        /// <summary>
        /// Performs a single POST to the remote evaluation endpoint.
        /// </summary>
        /// <returns>What the attempt produced, and whether another attempt could plausibly do better.</returns>
        private async Task<AttemptOutcome> SendAttemptAsync(
            HttpClient httpClient,
            string url,
            string jsonPayload,
            IDictionary<string, string> headers,
            TimeSpan? remainingBudget,
            CancellationToken cancellationToken)
        {
            // Cut the attempt off if it would run past what's left of the round's budget. The caller's token stays the
            // one the catch filters test, so a budget cut is classified as a timeout rather than as the caller giving up.
            using (var attemptCancellation = CreateAttemptCancellation(remainingBudget, cancellationToken))
            // A request message and its content can each only be sent once, so every attempt builds its own.
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                var attemptToken = attemptCancellation?.Token ?? cancellationToken;

                // Add custom headers
                if (headers != null)
                {
                    foreach (var header in headers)
                    {
                        httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                // Set content type
                httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                try
                {
                    _logger.LogDebug("Sending POST request to remote evaluation endpoint");

                    using (var response = await httpClient.SendAsync(httpRequest, attemptToken).ConfigureAwait(false))
                    {
                        var responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        // Same reasoning as the request: an evaluated payload can carry saved groups, which are
                        // typically lists of user identifiers.
                        _logger.LogDebug("Received response with status {StatusCode} and {CharacterCount} characters of content",
                            response.StatusCode, responseContent?.Length ?? 0);

                        if (response.IsSuccessStatusCode)
                        {
                            var apiResponse = JsonConvert.DeserializeObject<RemoteEvaluationResponse>(responseContent);
                            var featuresResponse = apiResponse.Features;

                            _logger.LogInformation("Remote evaluation successful, received {Count} features",
                                featuresResponse?.Count ?? 0);

                            return AttemptOutcome.Final(RemoteEvaluationResponse.CreateSuccess(featuresResponse));
                        }

                        var errorMessage = $"Remote evaluation failed with status {response.StatusCode}: {responseContent}";
                        var errorResponse = RemoteEvaluationResponse.CreateError(response.StatusCode, errorMessage);

                        if (IsRetryableStatusCode(response.StatusCode))
                        {
                            return AttemptOutcome.Retryable(errorResponse, $"status {(int)response.StatusCode}", GetRetryAfter(response));
                        }

                        // A 4xx that isn't 408 or 429 means this request is wrong rather than unlucky - a malformed
                        // payload or a bad client key - so repeating it would only fail the same way.
                        _logger.LogError(errorMessage);

                        return AttemptOutcome.Final(errorResponse);
                    }
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller asked to stop, which is not a failure to retry.
                    var errorMessage = "Remote evaluation request was cancelled";
                    _logger.LogWarning(ex, errorMessage);
                    throw new OperationCanceledException(errorMessage, ex, cancellationToken);
                }
                catch (TaskCanceledException ex)
                {
                    // HttpClient reports its own timeout as a cancellation, and only some platforms attach the
                    // TimeoutException. Since the caller's token isn't the one that fired, this is a timeout.
                    var errorMessage = "Remote evaluation request timed out";

                    return AttemptOutcome.Retryable(new RemoteEvaluationException(errorMessage, (int)HttpStatusCode.RequestTimeout, ex), "timed out");
                }
                catch (HttpRequestException ex)
                {
                    var errorMessage = $"HTTP error during remote evaluation: {ex.Message}";

                    return AttemptOutcome.Retryable(new RemoteEvaluationException(errorMessage, null, ex), ex.Message);
                }
                catch (JsonException ex)
                {
                    var errorMessage = $"Failed to parse remote evaluation response: {ex.Message}";
                    _logger.LogError(ex, errorMessage);

                    return AttemptOutcome.Final(new RemoteEvaluationException(errorMessage, 422, ex)); // 422 Unprocessable Entity
                }
                catch (Exception ex)
                {
                    var errorMessage = $"Unexpected error during remote evaluation: {ex.Message}";
                    _logger.LogError(ex, errorMessage);

                    return AttemptOutcome.Final(new RemoteEvaluationException(errorMessage, null, ex));
                }
            }
        }

        /// <summary>
        /// Builds the cancellation that bounds a single attempt to what's left of the round's budget, or nothing when
        /// the budget is unlimited.
        /// </summary>
        private static CancellationTokenSource CreateAttemptCancellation(TimeSpan? remainingBudget, CancellationToken cancellationToken)
        {
            if (!remainingBudget.HasValue)
            {
                return null;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            cancellation.CancelAfter(remainingBudget.Value > TimeSpan.Zero ? remainingBudget.Value : TimeSpan.Zero);

            return cancellation;
        }

        /// <summary>
        /// Determines whether a response status is worth another attempt.
        /// </summary>
        private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;

            // 429 is TooManyRequests, which netstandard2.0 has no enum member for.
            return code >= 500 || code == 429 || statusCode == HttpStatusCode.RequestTimeout;
        }

        /// <summary>
        /// Reads the delay a server asked for through a <c>Retry-After</c> header, in either of its two forms.
        /// </summary>
        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;

            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta;
            }

            if (retryAfter.Date.HasValue)
            {
                var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;

                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            return null;
        }

        /// <summary>
        /// What a single attempt produced: a response to hand back or a failure to raise, plus whether retrying it
        /// could plausibly help and any delay the server asked for.
        /// </summary>
        private class AttemptOutcome
        {
            private readonly RemoteEvaluationResponse _response;
            private readonly Exception _failure;

            private AttemptOutcome(RemoteEvaluationResponse response, Exception failure, bool isRetryable, string reason, TimeSpan? retryAfter)
            {
                _response = response;
                _failure = failure;
                IsRetryable = isRetryable;
                Reason = reason;
                RetryAfter = retryAfter;
            }

            /// <summary>Whether another attempt could plausibly succeed where this one didn't.</summary>
            public bool IsRetryable { get; }

            /// <summary>Short description of the failure, for logging.</summary>
            public string Reason { get; }

            /// <summary>The delay the server asked for, when it sent one.</summary>
            public TimeSpan? RetryAfter { get; }

            public static AttemptOutcome Final(RemoteEvaluationResponse response) =>
                new AttemptOutcome(response, null, isRetryable: false, reason: null, retryAfter: null);

            public static AttemptOutcome Final(Exception failure) =>
                new AttemptOutcome(null, failure, isRetryable: false, reason: failure?.Message, retryAfter: null);

            public static AttemptOutcome Retryable(RemoteEvaluationResponse response, string reason, TimeSpan? retryAfter) =>
                new AttemptOutcome(response, null, isRetryable: true, reason, retryAfter);

            public static AttemptOutcome Retryable(Exception failure, string reason) =>
                new AttemptOutcome(null, failure, isRetryable: true, reason, retryAfter: null);

            /// <summary>
            /// Hands back the response this attempt produced, or raises the failure it recorded.
            /// </summary>
            public RemoteEvaluationResponse Complete()
            {
                if (_failure != null)
                {
                    throw _failure;
                }

                return _response;
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
