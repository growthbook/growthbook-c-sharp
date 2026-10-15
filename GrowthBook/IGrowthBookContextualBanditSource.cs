using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Retrieves features together with the contextual bandit definitions they were published with.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IGrowthBookFeatureRepository"/> on purpose. This library targets
    /// netstandard2.0, which has no default interface members, so adding one to an existing public
    /// interface would break every consumer that implements it. The built-in repository implements this
    /// alongside, and the SDK asks for it with a type check - a custom repository keeps compiling and
    /// simply delivers no bandits until it opts in.
    /// <para>
    /// The pair is returned from the call that produced it rather than read from a property afterwards. A
    /// property holds one answer for the whole repository, and a repository is shared: a remote evaluation
    /// is specific to one user, so a second caller's response could replace it before the first caller
    /// read it, handing one user the features evaluated for another.
    /// </para>
    /// </remarks>
    public interface IGrowthBookContextualBanditSource
    {
        /// <summary>
        /// Retrieves the features as <see cref="IGrowthBookFeatureRepository.GetFeatures"/> does, paired
        /// with the definitions those features were published with.
        /// </summary>
        Task<FeaturePayload> GetPayload(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Retrieves the features as <see cref="IGrowthBookFeatureRepository.GetFeaturesWithContext"/>
        /// does, paired with the definitions that answer carried.
        /// </summary>
        Task<FeaturePayload> GetPayloadWithContext(Context context, GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);
    }
}
