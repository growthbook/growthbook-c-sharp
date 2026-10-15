namespace GrowthBook
{
    /// <summary>
    /// Publishes the last payload fetched, as one reference.
    /// </summary>
    /// <remarks>
    /// Implemented by the refresh worker, which fetches a payload but does not serve features on demand -
    /// that is the repository's job. The two halves are published together because a rule names a bandit
    /// and supplies its own variation list, so pairing one payload's rules with another's weights can
    /// bucket a user against a list that no longer exists.
    /// </remarks>
    public interface IFeaturePayloadSource
    {
        FeaturePayload Payload { get; }
    }
}
