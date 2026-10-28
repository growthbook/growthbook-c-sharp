namespace GrowthBook.Api
{
    /// <summary>
    /// Reports whether a repository can actually answer
    /// <see cref="IGrowthBookFeatureRepository.GetFeaturesWithContext"/> by evaluating remotely.
    /// </summary>
    /// <remarks>
    /// That call falls back to an ordinary cache read when remote evaluation is unavailable, and the
    /// features it returns look the same either way. A caller that treats a fallback read as a remote
    /// answer would record a user-specific result the instance never received.
    /// </remarks>
    public interface IRemoteEvaluationCapability
    {
        bool CanEvaluateRemotely { get; }
    }
}
