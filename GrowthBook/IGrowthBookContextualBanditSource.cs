namespace GrowthBook
{
    /// <summary>
    /// Supplies the most recent feature payload together with the contextual bandit definitions it carried.
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
        /// The last payload published, or null when none has been. Exposed as one object rather than as separate
        /// features and definitions so that a reader cannot pick up one from a newer payload than the other.
        /// </summary>
        FeaturePayload Payload { get; }
    }
}
