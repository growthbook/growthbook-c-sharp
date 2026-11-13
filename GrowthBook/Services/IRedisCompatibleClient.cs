namespace GrowthBook.Services
{
    /// <summary>
    /// The two Redis commands <see cref="RedisStickyBucketService"/> needs, so the SDK takes no dependency on a Redis
    /// client. Implement it over the client the application already uses.
    /// </summary>
    public interface IRedisCompatibleClient
    {
        /// <summary>
        /// Reads several keys at once, as <c>MGET</c>. Returns one value per key, in the same order, null where a key
        /// does not exist.
        /// </summary>
        string[] MultiGet(string[] keys);

        /// <summary>
        /// Writes a value, as <c>SET</c>.
        /// </summary>
        void Set(string key, string value);
    }
}
