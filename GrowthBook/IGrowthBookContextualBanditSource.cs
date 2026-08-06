using System.Collections.Generic;

namespace GrowthBook
{
    /// <summary>
    /// Supplies the contextual bandit definitions that arrived with the most recent feature payload.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IGrowthBookFeatureRepository"/> on purpose. This library targets
    /// netstandard2.0, which has no default interface members, so adding one to an existing public interface would
    /// break every consumer that implements it. Instead the built-in repository and refresh worker implement this
    /// alongside, and the SDK asks for it with a type check - a custom repository keeps compiling and simply
    /// delivers no bandits until it opts in by implementing this too.
    /// </remarks>
    public interface IGrowthBookContextualBanditSource
    {
        /// <summary>
        /// Definitions from the last payload, keyed by the reference a feature rule points at, or null when the
        /// payload carried none.
        /// </summary>
        IDictionary<string, ContextualBanditDefinition> ContextualBandits { get; }
    }
}
