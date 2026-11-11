namespace GrowthBook.Api.SSE
{
    /// <summary>
    /// Represents the connection status of a Server-Sent Events stream
    /// </summary>
    public enum SSEConnectionStatus
    {
        /// <summary>
        /// Connection is being established
        /// </summary>
        Connecting,

        /// <summary>
        /// Connection is active and receiving events
        /// </summary>
        Connected,

        /// <summary>
        /// Connection is disconnected
        /// </summary>
        Disconnected,

        /// <summary>
        /// No longer reported. The client retries indefinitely with a capped backoff rather than entering a
        /// terminal failure state, so a connection that is down is <see cref="Reconnecting"/> instead. Kept
        /// so that consumers switching on this enum still compile.
        /// </summary>
        Failed,

        /// <summary>
        /// Connection is attempting to reconnect
        /// </summary>
        Reconnecting
    }
}