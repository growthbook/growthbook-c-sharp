using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GrowthBook.Api.SSE
{
    /// <summary>
    /// Enhanced Server-Sent Events client with reconnection logic and event listeners
    /// </summary>
    public class SSEClient : IDisposable
    {
        private readonly ILogger<SSEClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _endpoint;
        private readonly Dictionary<string, string> _headers;
        private readonly string _httpClientName;
        private readonly Dictionary<string, Func<SSEEvent, Task>> _eventListeners;
        private readonly SSEEventParser _parser;

        private SSEConnectionStatus _connectionStatus;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _connectionTask;
        private string _lastEventId;
        private int _retryTimeMs = 3000; // Default retry time
        private int _maxRetryAttempts = 10;
        private int _currentRetryAttempt = 0;
        private System.Net.HttpStatusCode? _lastStatusCode; // Track last status code for reconnection logic

        public SSEConnectionStatus ConnectionStatus => _connectionStatus;
        public string LastEventId => _lastEventId;

        public event Action<SSEConnectionStatus> ConnectionStatusChanged;
        public event Action<Exception> ConnectionError;

        public SSEClient(ILogger<SSEClient> logger, IHttpClientFactory httpClientFactory, string endpoint, Dictionary<string, string> headers = null, string httpClientName = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _headers = headers ?? new Dictionary<string, string>();
            _httpClientName = httpClientName;
            _eventListeners = new Dictionary<string, Func<SSEEvent, Task>>();
            _parser = new SSEEventParser();
            _connectionStatus = SSEConnectionStatus.Disconnected;
        }

        /// <summary>
        /// Adds an event listener for a specific event type
        /// </summary>
        /// <param name="eventType">Event type to listen for (null for all events)</param>
        /// <param name="handler">Event handler function</param>
        public void AddEventListener(string eventType, Func<SSEEvent, Task> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var key = eventType ?? "*"; // Use "*" for all events
            _eventListeners[key] = handler;
        }

        /// <summary>
        /// Removes an event listener
        /// </summary>
        /// <param name="eventType">Event type to remove</param>
        public void RemoveEventListener(string eventType)
        {
            var key = eventType ?? "*";
            _eventListeners.Remove(key);
        }

        /// <summary>
        /// Connects to the SSE endpoint
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_connectionStatus == SSEConnectionStatus.Connected || _connectionStatus == SSEConnectionStatus.Connecting)
            {
                _logger.LogWarning("SSE client is already connected or connecting");
                return;
            }

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connectionTask = ConnectInternalAsync(_cancellationTokenSource.Token);
            await _connectionTask;
        }

        /// <summary>
        /// Disconnects from the SSE endpoint
        /// </summary>
        public void Disconnect()
        {
            _cancellationTokenSource?.Cancel();
            SetConnectionStatus(SSEConnectionStatus.Disconnected);
            _currentRetryAttempt = 0;
        }

        private async Task ConnectInternalAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _currentRetryAttempt < _maxRetryAttempts)
            {
                try
                {
                    SetConnectionStatus(SSEConnectionStatus.Connecting);
                    _logger.LogInformation("Connecting to SSE endpoint: {Endpoint}", _endpoint);

                    var httpClient = string.IsNullOrEmpty(_httpClientName) 
                        ? _httpClientFactory.CreateClient() 
                        : _httpClientFactory.CreateClient(_httpClientName);
                    
                    // Set headers
                    foreach (var header in _headers)
                    {
                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    // Add Last-Event-ID header if we have one
                    if (!string.IsNullOrEmpty(_lastEventId))
                    {
                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Last-Event-ID", _lastEventId);
                    }

                    // Set SSE-specific headers
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream");
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");

                    using (var response = await httpClient.GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        _lastStatusCode = response.StatusCode; // Store status code for reconnection logic
                        
                        if (!response.IsSuccessStatusCode)
                        {
                            if (IsSubscriptionOver(response.StatusCode))
                            {
                               _logger.LogInformation("SSE endpoint returned {StatusCode}, the subscription is over and will not be retried", response.StatusCode);
                                SetConnectionStatus(SSEConnectionStatus.Disconnected);
                                break;
                            }

                            throw new HttpRequestException($"SSE connection failed with status code: {response.StatusCode}");
                        }

                        SetConnectionStatus(SSEConnectionStatus.Connected);

                        var deliveredEvents = false;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            deliveredEvents = await ProcessStreamAsync(reader, cancellationToken);
                        }

                        if (deliveredEvents)
                        {
                           _currentRetryAttempt = 0;
                        }

                        if (!ShouldReconnect(_lastStatusCode.Value))
                        {
                            _logger.LogInformation("SSE connection closed with status {StatusCode}, not reconnecting", _lastStatusCode.Value);
                            break;
                        }

                        _logger.LogInformation("SSE connection closed with status {StatusCode}, attempting to reconnect...", _lastStatusCode.Value);

                        if (!await WaitBeforeReconnecting(cancellationToken))
                        {
                            break;
                        }

                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("SSE connection was cancelled");
                    SetConnectionStatus(SSEConnectionStatus.Disconnected);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SSE connection error (attempt {Attempt}/{MaxAttempts})", _currentRetryAttempt + 1, _maxRetryAttempts);
                    
                    ConnectionError?.Invoke(ex);
                    
                    _currentRetryAttempt++;
                    
                    if (_currentRetryAttempt < _maxRetryAttempts)
                    {
                        SetConnectionStatus(SSEConnectionStatus.Reconnecting);
                        var delay = CalculateRetryDelay();
                        _logger.LogInformation("Retrying SSE connection in {Delay}ms", delay);
                        
                        try
                        {
                            await Task.Delay(delay, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    else
                    {
                        SetConnectionStatus(SSEConnectionStatus.Failed);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Reads the stream to its end, returning whether it delivered any event. A connection that
        /// delivered nothing is not evidence that the endpoint is healthy.
        /// </summary>
        private async Task<bool> ProcessStreamAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            var deliveredEvents = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    _logger.LogDebug("SSE stream ended");
                    break;
                }

                var data = new string(buffer, 0, bytesRead);
                var events = _parser.AppendData(data);

                foreach (var sseEvent in events)
                {
                    deliveredEvents = true;
                    await ProcessEventAsync(sseEvent);
                }
            }

            return deliveredEvents;
        }

        private async Task ProcessEventAsync(SSEEvent sseEvent)
        {
            _logger.LogDebug("Received SSE event: {Event}", sseEvent);

            // Update last event ID
            if (!string.IsNullOrEmpty(sseEvent.Id))
            {
                _lastEventId = sseEvent.Id;
            }

            // Update retry time
            if (sseEvent.RetryTime.HasValue)
            {
                _retryTimeMs = sseEvent.RetryTime.Value;
            }

            // Skip retry-only events
            if (sseEvent.IsRetryOnlyEvent)
            {
                return;
            }

            // Call specific event listener
            if (!string.IsNullOrEmpty(sseEvent.Event) && _eventListeners.TryGetValue(sseEvent.Event, out var specificHandler))
            {
                try
                {
                    await specificHandler(sseEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SSE event handler for event type: {EventType}", sseEvent.Event);
                }
            }

            // Call general event listener
            if (_eventListeners.TryGetValue("*", out var generalHandler))
            {
                try
                {
                    await generalHandler(sseEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in general SSE event handler");
                }
            }
        }

        private int CalculateRetryDelay()
        {
            // Exponential backoff with jitter. The exponent counts the attempts already made rather than this
            // one, so the first reconnect waits exactly the time the server asked for with the `retry:` field
            // instead of doubling it before anything has gone wrong twice.
            var completedAttempts = Math.Max(_currentRetryAttempt - 1, 0);
            var baseDelay = Math.Min(_retryTimeMs * Math.Pow(2, completedAttempts), 30000); // Max 30 seconds

            // Jitter in proportion to the delay. A flat second on top would dominate the short retry
            // times a server can ask for with the SSE `retry:` field.
            var jitterCeiling = (int)Math.Min(baseDelay / 2, 1000);
            var jitter = 0;

            if (jitterCeiling > 0)
            {
                jitter = new Random().Next(0, jitterCeiling);
            }

            return (int)baseDelay + jitter;
        }

        /// <summary>
        /// Determines if SSE connection should be reconnected based on status code
        /// </summary>
        /// <param name="statusCode">HTTP status code</param>
        /// <returns>True if status code is 200-299, false otherwise</returns>
        private bool ShouldReconnect(System.Net.HttpStatusCode statusCode)
        {
            var statusCodeInt = (int)statusCode;
            return statusCodeInt >= 200 && statusCodeInt < 300;
        }

        /// <summary>
        /// Whether the server has refused the subscription outright rather than failed to serve it this time.
        /// Retrying one of these repeats a request that cannot start succeeding on its own: the subscription
        /// has ended, or the key it was made with is not one this endpoint will accept.
        /// </summary>
        private static bool IsSubscriptionOver(System.Net.HttpStatusCode statusCode)
        {
            switch (statusCode)
            {
                case System.Net.HttpStatusCode.Gone:
                case System.Net.HttpStatusCode.Unauthorized:
                case System.Net.HttpStatusCode.Forbidden:
                case System.Net.HttpStatusCode.NotFound:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Counts a reconnect and waits out the backoff before it. Returns false when the attempts are
        /// spent or the wait was cancelled, meaning the caller should stop.
        /// </summary>
        private async Task<bool> WaitBeforeReconnecting(CancellationToken cancellationToken)
        {
            _currentRetryAttempt++;

            if (_currentRetryAttempt >= _maxRetryAttempts)
            {
                _logger.LogWarning("SSE connection gave up after {Attempts} attempts", _currentRetryAttempt);
                SetConnectionStatus(SSEConnectionStatus.Disconnected);

                return false;
            }

            SetConnectionStatus(SSEConnectionStatus.Reconnecting);

            var delay = CalculateRetryDelay();
            _logger.LogInformation("Retrying SSE connection in {Delay}ms", delay);

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SetConnectionStatus(SSEConnectionStatus.Disconnected);

                return false;
            }

            return true;
        }

        private void SetConnectionStatus(SSEConnectionStatus status)
        {
            if (_connectionStatus != status)
            {
                _connectionStatus = status;
                ConnectionStatusChanged?.Invoke(status);
                _logger.LogDebug("SSE connection status changed to: {Status}", status);
            }
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource?.Dispose();
        }
    }
}