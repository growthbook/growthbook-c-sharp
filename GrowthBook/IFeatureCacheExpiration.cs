using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Implemented by a cache that can have its lifetime restarted without its contents being rewritten.
    /// </summary>
    /// <remarks>
    /// An HTTP 304 says the cached features are still current, so the window should start again and
    /// nothing should be written. Without a way to say that, the cache stays expired and every later
    /// load goes back to the API, which is the request storm a 304 exists to avoid.
    /// <para>
    /// A separate interface rather than a member on <see cref="IGrowthBookFeatureCache"/>, because this
    /// library targets netstandard2.0 and has no default interface members - adding one there would stop
    /// every caller's own cache compiling. A cache that does not implement this simply misses the
    /// optimisation, as it did before.
    /// </para>
    /// </remarks>
    public interface IFeatureCacheExpiration
    {
        /// <summary>
        /// Restarts the cache's lifetime, leaving the features it holds untouched.
        /// </summary>
        Task RefreshExpiration(CancellationToken? cancellationToken = null);
    }
}
