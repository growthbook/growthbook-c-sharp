using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook.Services
{
    /// <summary>
    /// An asynchronous sticky bucket service, for backing stores that are async by nature
    /// (Redis, SQL, a distributed cache, an HTTP API).
    /// </summary>
    /// <remarks>
    /// Implement this instead of <see cref="IStickyBucketService"/> when your store is async, and set it on
    /// <see cref="Context.AsyncStickyBucketService"/>. Implementing the synchronous interface over an async
    /// store would force a blocking <c>.GetAwaiter().GetResult()</c> inside evaluation, which deadlocks under
    /// a captured <c>SynchronizationContext</c> (UI threads, classic ASP.NET).
    /// <para>
    /// Feature evaluation itself stays synchronous. Assignments are read once, up front, by
    /// <c>GrowthBook.LoadStickyBucketAssignmentsAsync</c> (also called by <c>LoadFeatures</c>), and writes are
    /// dispatched without being awaited so evaluation never blocks on the store. If you change attributes on a
    /// long-lived instance, call <c>LoadStickyBucketAssignmentsAsync</c> again to pick up assignments for the
    /// new identifier.
    /// </para>
    /// </remarks>
    public interface IAsyncStickyBucketService
    {
        /// <summary>
        /// Gets the assignment document for a single attribute name/value pair.
        /// </summary>
        /// <param name="attributeName">The attribute name, e.g. "id".</param>
        /// <param name="attributeValue">The attribute value, e.g. "user-123".</param>
        /// <param name="cancellationToken">Used for monitoring the need to cancel the retrieval.</param>
        /// <returns>The matching document, or <c>null</c> when the store has nothing for that pair.</returns>
        Task<StickyAssignmentsDocument> GetAssignmentsAsync(string attributeName, string attributeValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists an assignment document.
        /// </summary>
        /// <param name="document">The document to persist.</param>
        /// <param name="cancellationToken">Used for monitoring the need to cancel the save.</param>
        /// <returns>A <see cref="Task"/> representing the save operation.</returns>
        Task SaveAssignmentsAsync(StickyAssignmentsDocument document, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets every assignment document matching the provided formatted attribute keys. Implementations backed
        /// by a store that supports batch reads (e.g. Redis MGET) should do a single round trip here rather than
        /// looping over <see cref="GetAssignmentsAsync"/>.
        /// </summary>
        /// <param name="attributes">
        /// Formatted attribute keys in <c>attributeName||attributeValue</c> form, matching
        /// <see cref="StickyAssignmentsDocument.FormattedAttribute"/>.
        /// </param>
        /// <param name="cancellationToken">Used for monitoring the need to cancel the retrieval.</param>
        /// <returns>Documents keyed by their formatted attribute. Keys with nothing stored are omitted.</returns>
        Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default);
    }
}
