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
        
        private const int MaxRetryDelayMs = 5 * 60 * 1000;
        private static readonly Random JitterSource = new Random();

        private SSEConnectionStatus _connectionStatus;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _connectionTask;
        private string _lastEventId;
        private int _retryTimeMs = 3000; // Default retry time
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
            while (!cancellationToken.IsCancellationRequested)
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

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            await ProcessStreamAsync(reader, cancellationToken);
                        }
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        SetConnectionStatus(SSEConnectionStatus.Disconnected);
                        break;
                    }

                    // The server ended the stream without an error. This still needs to go through the
                    // backoff below - looping straight back into a reconnect here would spin as fast as the
                    // server can accept and close, hammering the API.
                    _logger.LogInformation("SSE stream was closed by the server, will attempt to reconnect");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("SSE connection was cancelled");
                    SetConnectionStatus(SSEConnectionStatus.Disconnected);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SSE connection error (consecutive failure {Attempt})", _currentRetryAttempt + 1);

                    ConnectionError?.Invoke(ex);
                }

                // The counter is only reset by ProcessStreamAsync, on data actually arriving. Resetting
                // merely because the socket opened would let a server that accepts and immediately closes
                // keep it at zero forever, so the growing delay would never apply. It exists solely to grow
                // the delay - there is deliberately no attempt limit, so a long outage degrades to one
                // attempt per MaxRetryDelayMs rather than stopping the client for the process lifetime.
                _currentRetryAttempt++;

                SetConnectionStatus(SSEConnectionStatus.Reconnecting);

                var retryDelay = CalculateRetryDelay();
                _logger.LogInformation("Retrying SSE connection in {Delay}ms", retryDelay);

                try
                {
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SetConnectionStatus(SSEConnectionStatus.Disconnected);
                    break;
                }
            }
        }

        /// <summary>
        /// Reads the stream until it ends or the token is cancelled.
        /// </summary>
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

                // Delivered data means this connection is productive, so the reconnect backoff starts
                // over. This has to happen here rather than after the read loop finishes: a connection
                // that streams fine for hours and then drops with an exception never reaches code below
                // the loop, so the attempt counter would keep climbing across unrelated drops until the
                // client hit _maxRetryAttempts and gave up permanently. Mirrors the reference SDK, which
                // resets its error count on every message received.
                _currentRetryAttempt = 0;

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
            double jitterFactor;

            lock (JitterSource)
            {
                jitterFactor = 1d + JitterSource.NextDouble();
            }

            var delay = _retryTimeMs * Math.Pow(2, _currentRetryAttempt - 1) * jitterFactor;

            return (int)Math.Min(delay, MaxRetryDelayMs);
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