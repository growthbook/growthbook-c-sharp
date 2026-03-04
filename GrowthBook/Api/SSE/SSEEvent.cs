using System;

namespace GrowthBook.Api.SSE
{
    /// <summary>
    /// Represents a Server-Sent Event with all standard SSE fields
    /// </summary>
    public class SSEEvent
    {
        /// <summary>
        /// Event ID for reconnection purposes
        /// </summary>
        public string? Id { get; set; }

        /// <summary>
        /// Event type/name
        /// </summary>
        public string? Event { get; set; }

        /// <summary>
        /// Event data payload
        /// </summary>
        public string? Data { get; set; }

        /// <summary>
        /// Retry time in milliseconds for reconnection
        /// </summary>
        public int? RetryTime { get; set; }

        /// <summary>
        /// Indicates if this event only contains retry information
        /// </summary>
        public bool IsRetryOnlyEvent => string.IsNullOrEmpty(Id) && 
                                       string.IsNullOrEmpty(Event) && 
                                       string.IsNullOrEmpty(Data) && 
                                       RetryTime.HasValue;

        /// <summary>
        /// Indicates if this event has actual data content
        /// </summary>
        public bool HasData => !string.IsNullOrEmpty(Data);

        public SSEEvent()
        {
        }

        public SSEEvent(string id, string eventType, string data, int? retryTime = null)
        {
            Id = id;
            Event = eventType;
            Data = data;
            RetryTime = retryTime;
        }

        public override string ToString()
        {
            return $"SSEEvent [Id={Id}, Event={Event}, Data={Data?.Substring(0, Math.Min(Data.Length, 50))}..., RetryTime={RetryTime}]";
        }
    }
}