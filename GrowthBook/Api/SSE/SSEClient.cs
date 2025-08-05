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
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"SSE connection failed with status code: {response.StatusCode}");
                        }

                        SetConnectionStatus(SSEConnectionStatus.Connected);
                        _currentRetryAttempt = 0; // Reset retry counter on successful connection
                        
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            await ProcessStreamAsync(reader, cancellationToken);
                        }
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

        private async Task ProcessStreamAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            
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
                    await ProcessEventAsync(sseEvent);
                }
            }
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
            // Exponential backoff with jitter
            var baseDelay = Math.Min(_retryTimeMs * Math.Pow(2, _currentRetryAttempt), 30000); // Max 30 seconds
            var jitter = new Random().Next(0, 1000); // Add up to 1 second jitter
            return (int)baseDelay + jitter;
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