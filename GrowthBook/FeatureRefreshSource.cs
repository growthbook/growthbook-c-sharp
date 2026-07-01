namespace GrowthBook
{
    /// <summary>
    /// Identifies how a feature refresh was delivered.
    /// </summary>
    public enum FeatureRefreshSource
    {
        /// <summary>
        /// The refresh came from an HTTP request to the Features API (a 200 with new
        /// features or a 304 Not Modified confirming the cache).
        /// </summary>
        Http,

        /// <summary>
        /// The refresh was pushed over a server-sent events (SSE) stream.
        /// </summary>
        ServerSentEvent
    }
}
