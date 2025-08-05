using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GrowthBook.Api.SSE
{
    /// <summary>
    /// Parser for Server-Sent Events according to the SSE specification
    /// </summary>
    public class SSEEventParser
    {
        private readonly StringBuilder _buffer = new StringBuilder();
        private static readonly string[] NewLineCharacters = { "\r\n", "\n", "\r" };

        /// <summary>
        /// Appends data to the internal buffer and returns any complete events
        /// </summary>
        /// <param name="data">Raw data from SSE stream</param>
        /// <returns>List of parsed SSE events</returns>
        public IEnumerable<SSEEvent> AppendData(string data)
        {
            if (string.IsNullOrEmpty(data))
                return Enumerable.Empty<SSEEvent>();

            _buffer.Append(data);
            return ExtractEventsFromBuffer();
        }

        /// <summary>
        /// Parses a single SSE event string into an SSEEvent object
        /// </summary>
        /// <param name="eventString">Raw event string</param>
        /// <returns>Parsed SSEEvent or null if invalid</returns>
        public static SSEEvent ParseEvent(string eventString)
        {
            if (string.IsNullOrEmpty(eventString) || eventString.StartsWith(":"))
                return null; // Comments start with ":"

            var eventData = new Dictionary<string, string>();
            var lines = eventString.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;

                var (key, value) = ParseLine(line);
                if (string.IsNullOrEmpty(key))
                    continue;

                // If field already exists, append with newline
                if (eventData.ContainsKey(key))
                {
                    eventData[key] += "\n" + (value ?? "");
                }
                else
                {
                    eventData[key] = value ?? "";
                }
            }

            // Create SSEEvent from parsed fields
            var sseEvent = new SSEEvent
            {
                Id = eventData.TryGetValue("id", out var id) ? id : null,
                Event = eventData.TryGetValue("event", out var eventType) ? eventType : null,
                Data = eventData.TryGetValue("data", out var data) ? data : null
            };

            // Parse retry time
            if (eventData.TryGetValue("retry", out var retryString) && 
                int.TryParse(retryString.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retryTime))
            {
                sseEvent.RetryTime = retryTime;
            }

            return sseEvent;
        }

        /// <summary>
        /// Parses a single line of SSE data
        /// </summary>
        /// <param name="line">Line to parse</param>
        /// <returns>Tuple of (key, value)</returns>
        private static (string key, string value) ParseLine(string line)
        {
            var colonIndex = line.IndexOf(':');
            
            if (colonIndex == -1)
            {
                // No colon found, treat entire line as field name with empty value
                return (line.Trim(), "");
            }

            var key = line.Substring(0, colonIndex).Trim();
            var value = colonIndex + 1 < line.Length ? line.Substring(colonIndex + 1) : "";

            // Remove single leading space from value (per SSE spec)
            if (value.StartsWith(" "))
                value = value.Substring(1);

            return (key, value);
        }

        /// <summary>
        /// Extracts complete events from the internal buffer
        /// </summary>
        /// <returns>List of parsed events</returns>
        private IEnumerable<SSEEvent> ExtractEventsFromBuffer()
        {
            var events = new List<SSEEvent>();
            var bufferContent = _buffer.ToString();
            
            // Events are separated by double newlines
            var eventDelimiters = new[] { "\r\n\r\n", "\n\n", "\r\r" };
            var parts = bufferContent.Split(eventDelimiters, StringSplitOptions.None);

            // Process all complete events (all but the last part)
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var eventString = parts[i];
                if (!string.IsNullOrEmpty(eventString))
                {
                    var sseEvent = ParseEvent(eventString);
                    if (sseEvent != null)
                    {
                        events.Add(sseEvent);
                    }
                }
            }

            // Keep the last incomplete part in the buffer
            if (parts.Length > 0)
            {
                var lastPart = parts[parts.Length - 1];
                _buffer.Clear();
                _buffer.Append(lastPart);
            }

            return events;
        }

        /// <summary>
        /// Clears the internal buffer
        /// </summary>
        public void ClearBuffer()
        {
            _buffer.Clear();
        }
    }
}