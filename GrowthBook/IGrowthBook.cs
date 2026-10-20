using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthBook
{
    /// <summary>
    /// Providing operations to interact with feature flags.
    /// </summary>
    public interface IGrowthBook : IDisposable
    {
        /// <summary>
        /// Checks to see if a feature is on.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is on.</returns>
        bool IsOn(string key);

        /// <summary>
        /// Checks to see if a feature is off.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <returns>True if the feature is off.</returns>
        bool IsOff(string key);


        /// <summary>
        /// Asynchronously checks whether a feature is on (enabled) for the current context.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that resolves to <c>true</c> if the feature is on; otherwise, <c>false</c>.</returns>
        Task<bool> IsOnAsync(string key, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Asynchronously checks whether a feature is off (disabled) for the current context.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A task that resolves to <c>true</c> if the feature is off; otherwise, <c>false</c>.</returns>
        Task<bool> IsOffAsync(string key, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Subscribes a synchronous callback to feature evaluations.
        /// The callback is invoked every time a feature or experiment is evaluated.
        /// </summary>
        /// <param name="callback">The callback to invoke after each evaluation.</param>
        /// <returns>An <see cref="IDisposable"/> that can be disposed to unsubscribe.</returns>
        IDisposable Subscribe(Action<Experiment, ExperimentResult> callback);

        /// <summary>
        /// Subscribes an asynchronous callback to feature evaluations.
        /// The callback is awaited asynchronously in a fire-and-forget manner without blocking.
        /// </summary>
        /// <param name="callback">The asynchronous callback to invoke after each evaluation.</param>
        /// <returns>An <see cref="IDisposable"/> that can be disposed to unsubscribe.</returns>
        IDisposable SubscribeAsync(Func<Experiment, ExperimentResult, Task> callback);

        /// <summary>
        /// Gets the value of a feature cast to the specified type. This is a blocking operation and should not be used from a UI thread.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The feature key.</param>
        /// <param name="fallback">Fallback value to return if the feature is not on.</param>
        /// <param name="alwaysLoadFeatures">
        /// Loads all features from the repository/cache prior to executing.
        /// This is included for backwards compatibility and, when set to true, becomes a blocking operation and should not be used from a UI thread.
        /// If possible, please use the async version of this method: <see cref="GetFeatureValueAsync{T}(string, T, CancellationToken?)"/>
        /// </param>
        /// <returns>Value of a feature cast to the specified type.</returns>
        T GetFeatureValue<T>(string key, T fallback, bool alwaysLoadFeatures = false);

        /// <summary>
        /// Asynchronously gets the value of a feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key">The feature key.</param>
        /// <param name="fallback">Fallback value to return if the feature is not on.</param>
        /// <param name="cancellationToken">The cancellation token for this operation.</param>
        /// <returns>Value of a feature cast to the specified type.</returns>
        Task<T> GetFeatureValueAsync<T>(string key, T fallback, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Returns a map of the latest results indexed by experiment key.
        /// </summary>
        /// <returns></returns>
        IDictionary<string, ExperimentAssignment> GetAllResults();

        /// <summary>
        /// Evaluates a feature and returns a feature result.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="alwaysLoadFeatures">
        /// Loads all features from the feature repository/cache prior to executing.
        /// This is included for backwards compatibility and, when set to true, becomes a blocking operation and should not be used from a UI thread.
        /// If possible, please use the async version of this method: <see cref="EvalFeatureAsync(string, CancellationToken?)"/>
        /// </param>
        /// <returns>The feature result.</returns>
        FeatureResult EvalFeature(string key, bool alwaysLoadFeatures = false);

        /// <summary>
        /// Asynchronously loads and evaluates a feature and returns a feature result.
        /// </summary>
        /// <param name="featureId">The feature ID.</param>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        /// <returns>The feature result.</returns>
        Task<FeatureResult> EvalFeatureAsync(string featureId, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Evaluates an experiment and returns an experiment result.
        /// </summary>
        /// <param name="experiment">The experiment to evaluate.</param>
        /// <returns>The experiment result.</returns>
        ExperimentResult Run(Experiment experiment);

        /// <summary>
        /// Replaces all user attributes with the ones provided.
        /// </summary>
        /// <remarks>
        /// This is a full replace: any attribute that isn't present in <paramref name="attributes"/> is dropped.
        /// Use <see cref="MergeAttributes(IDictionary{string, object})"/> to merge into the existing attributes instead.
        /// </remarks>
        /// <param name="attributes">New user attributes as IDictionary, or null to clear all attributes.</param>
        void UpdateAttributes(IDictionary<string, object> attributes);

        /// <summary>
        /// Replaces all user attributes with the ones provided.
        /// </summary>
        /// <remarks>
        /// This is a full replace: any attribute that isn't present in <paramref name="attributes"/> is dropped.
        /// Use <see cref="MergeAttributes(object)"/> to merge into the existing attributes instead.
        /// </remarks>
        /// <param name="attributes">New user attributes as an anonymous object, or null to clear all attributes.</param>
        void UpdateAttributes(object attributes);

        /// <summary>
        /// Merges additional attributes into the existing ones.
        /// </summary>
        /// <remarks>
        /// This is a shallow merge: new keys are added, existing keys are overwritten, and keys that aren't present
        /// in <paramref name="additionalAttributes"/> are preserved. Passing null is a no-op.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge.</param>
        void MergeAttributes(IDictionary<string, object> additionalAttributes);

        /// <summary>
        /// Merges additional attributes into the existing ones.
        /// </summary>
        /// <remarks>
        /// This is a shallow merge: new keys are added, existing keys are overwritten, and keys that aren't present
        /// in <paramref name="additionalAttributes"/> are preserved. Passing null is a no-op.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge as an anonymous object.</param>
        void MergeAttributes(object additionalAttributes);

        /// <summary>
        /// Replaces all user attributes with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <param name="attributes">New user attributes as IDictionary, or null to clear all attributes.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        Task UpdateAttributesAsync(IDictionary<string, object> attributes, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Replaces all user attributes with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <param name="attributes">New user attributes as an anonymous object, or null to clear all attributes.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        Task UpdateAttributesAsync(object attributes, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Merges additional attributes into the existing ones and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <param name="additionalAttributes">Additional attributes to merge.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the merge and any remote evaluation it triggered.</returns>
        Task MergeAttributesAsync(IDictionary<string, object> additionalAttributes, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Merges additional attributes into the existing ones and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <param name="additionalAttributes">Additional attributes to merge as an anonymous object.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the merge and any remote evaluation it triggered.</returns>
        Task MergeAttributesAsync(object additionalAttributes, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Replaces the forced variations with the ones provided.
        /// </summary>
        /// <remarks>
        /// Forced variations are part of the remote evaluation payload, so this starts a remote evaluation in the
        /// background when the change requires one.
        /// </remarks>
        /// <param name="forcedVariations">The experiment keys to force to a specific variation, or null to clear them.</param>
        void SetForcedVariations(IDictionary<string, int> forcedVariations);

        /// <summary>
        /// Replaces the forced variations with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <param name="forcedVariations">The experiment keys to force to a specific variation, or null to clear them.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        Task SetForcedVariationsAsync(IDictionary<string, int> forcedVariations, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Loads all available features from the API and caches them for faster retrieval.
        /// </summary>
        /// <param name="options">An optional set of choices that affect how the features will be loaded.</param>
        /// <returns>A <see cref="Task"/> that represents the feature retrieval action.</returns>
        Task LoadFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);

        /// <summary>
        /// Loads all available features from the API and returns detailed result information.
        /// </summary>
        /// <param name="options">An optional set of choices that affect how the features will be loaded.</param>
        /// <param name="cancellationToken">The cancellation token for this operation.</param>
        /// <returns>A <see cref="FeatureLoadResult"/> indicating success or failure with details.</returns>
        Task<FeatureLoadResult> LoadFeaturesWithResult(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null);
    }
}
