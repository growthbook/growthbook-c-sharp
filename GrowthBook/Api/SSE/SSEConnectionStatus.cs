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
        /// Connection failed due to an error
        /// </summary>
        Failed,

        /// <summary>
        /// Connection is attempting to reconnect
        /// </summary>
        Reconnecting
    }
}