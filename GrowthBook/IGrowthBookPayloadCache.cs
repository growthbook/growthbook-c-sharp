using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Stores a payload's features together with the contextual bandit definitions they were published with.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IGrowthBookFeatureCache"/> on purpose. This library targets netstandard2.0,
    /// which has no default interface members, so adding one to an existing public interface would break every
    /// consumer that implements it. <see cref="Api.InMemoryFeatureCache"/> implements this alongside, and the SDK
    /// asks for it with a type check - a cache written before bandits existed keeps compiling and simply serves no
    /// definitions.
    /// <para>
    /// That is the whole reason this interface exists rather than the two halves being stored side by side. A cache
    /// holding features alone cannot say which payload they came from, so any attempt to pair them afterwards with
    /// definitions held elsewhere is a guess. A shared cache refreshed by another process is the case that makes the
    /// guess wrong: the features change while the definitions beside them do not, and a user gets bucketed against a
    /// variation list that is not the one being served. Storing and returning the pair removes the guess.
    /// </para>
    /// </remarks>
    public interface IGrowthBookPayloadCache
    {
        /// <summary>
        /// Gets the cached payload, read as one value so that a refresh in progress cannot hand back this payload's
        /// features beside the previous one's definitions.
        /// </summary>
        /// <param name="cancellationToken">Used to monitor whether the retrieval action should be cancelled.</param>
        /// <returns>A <see cref="Task"/> that represents the retrieval action.</returns>
        Task<FeaturePayload> GetPayload(CancellationToken? cancellationToken = null);

        /// <summary>
        /// Replaces the cached payload with the one supplied, features and definitions together.
        /// </summary>
        /// <param name="payload">The payload to cache.</param>
        /// <param name="cancellationToken">Used to monitor whether the cache action should be cancelled.</param>
        /// <returns>A <see cref="Task"/> that represents the refresh action.</returns>
        Task RefreshWith(FeaturePayload payload, CancellationToken? cancellationToken = null);
    }
}
