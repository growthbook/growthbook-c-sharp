using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook.Services
{
    /// <summary>
    /// The two Redis operations <see cref="RedisStickyBucketService"/> needs, declared here so the SDK does
    /// not take a dependency on any particular Redis client. Adapt whichever one the application already
    /// uses - StackExchange.Redis, a wrapper, a test double.
    /// </summary>
    /// <remarks>
    /// Mirrors the reference SDK's <c>IORedisCompat</c>, which is likewise a local contract rather than an
    /// import of a client library.
    /// </remarks>
    public interface IRedisCompatibleClient
    {
        /// <summary>
        /// Reads several keys at once, returning one entry per key in the order they were given. Missing
        /// keys must be represented by a null entry rather than omitted, so results line up with the keys.
        /// </summary>
        Task<string[]> MultiGetAsync(string[] keys, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores a value under a key, overwriting whatever was there.
        /// </summary>
        Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    }
}
