using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using GrowthBook.Api;
using GrowthBook.Extensions;
using GrowthBook.Providers;
using GrowthBook.Services;
using GrowthBook.Utilities;
using GrowthBook.Exceptions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// This is the C# client library for GrowthBook, the open-source
    //  feature flagging and A/B testing platform.
    //  More info at https://www.growthbook.io
    /// </summary>
    public class GrowthBook : IGrowthBook, IDisposable
    {
        private readonly bool _qaMode;
        private readonly Dictionary<string, ExperimentAssignment> _assigned;
        private readonly ConcurrentDictionary<string, byte> _tracked;
        private Action<Experiment, ExperimentResult> _trackingCallback;
        private IDictionary<string, JToken> _forcedFeatures;
        private bool _disposedValue;
        private readonly IConditionEvaluationProvider _conditionEvaluator;
        private readonly IGrowthBookFeatureRepository _featureRepository;
        private readonly IStickyBucketService _stickyBucketService;
        private readonly IAsyncStickyBucketService _asyncStickyBucketService;

        /// <summary>
        /// Whether sticky bucketing is available at all, regardless of which of the two service flavors
        /// was configured. Reads always come from <see cref="_stickyBucketAssignmentDocs"/>, so the read
        /// path doesn't care which store filled them.
        /// </summary>
        private bool IsStickyBucketingEnabled => _stickyBucketService != null || _asyncStickyBucketService != null;
        /// <summary>
        /// Replaced wholesale on a refresh rather than mutated in place. A concurrent evaluation reads this
        /// dictionary, so clearing it before repopulating would let that evaluation see an empty or half-filled
        /// set - and <see cref="Dictionary{TKey, TValue}"/> is not safe for a concurrent read during a write at
        /// all. The reference SDK publishes refreshed docs the same way, by assigning the whole collection.
        /// </summary>
        private volatile IDictionary<string, StickyAssignmentsDocument> _stickyBucketAssignmentDocs;

        private readonly object _stickyBucketWriteLock = new object();
        private long _stickyBucketLoadSequence;
        private long _publishedStickyBucketLoad;
        private readonly ILogger<GrowthBook> _logger;
        private readonly JObject _savedGroups;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;
        private readonly Context _context;
        private readonly object _attributesLock = new object();
        private JObject _attributes;
        private IDictionary<string, int> _forcedVariations;
        private JObject _previousAttributes;
        private IDictionary<string, int> _previousForcedVariations;
        private Task _pendingRemoteEvaluation;
        private long _remoteEvaluationGeneration;
        private readonly List<Action<Experiment, ExperimentResult>> _subscribers
            = new List<Action<Experiment, ExperimentResult>>();
        private readonly List<Func<Experiment, ExperimentResult, Task>> _asyncSubscribers
            = new List<Func<Experiment, ExperimentResult, Task>>();

        /// <summary>
        /// Creates a new GrowthBook instance from the passed context.
        /// </summary>
        /// <param name="context">The GrowthBook Context object.</param>
        public GrowthBook(Context context)
        {
            ValidateRemoteEvaluationConfiguration(context);
            ValidateStickyBucketConfiguration(context);

            _context = context;
            Enabled = context.Enabled;
            Url = context.Url;
            Features = context.Features?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, Feature>();
            Experiments = context.Experiments ?? new List<Experiment>();

            // Assigned through the backing fields rather than the properties: the property setters start a remote
            // evaluation on change, and there is nothing to refresh yet while the instance is still being built.
            _attributes = context.Attributes;
            _forcedVariations = context.ForcedVariations;

            _qaMode = context.QaMode;
            _trackingCallback = context.TrackingCallback;
            if (context.ForcedFeatures != null)
            {
                _forcedFeatures = new Dictionary<string, JToken>(context.ForcedFeatures);
            }
            else
            {
                _forcedFeatures = new Dictionary<string, JToken>();
            }
            _assigned = new Dictionary<string, ExperimentAssignment>();
            _tracked = new ConcurrentDictionary<string, byte>();
            _stickyBucketService = context.StickyBucketService;
            _asyncStickyBucketService = context.AsyncStickyBucketService;
            _stickyBucketAssignmentDocs = context.StickyBucketAssignmentDocs ?? new Dictionary<string, StickyAssignmentsDocument>();
            _savedGroups = context.SavedGroups;
            _previousAttributes = context.Attributes?.DeepClone() as JObject;
            _previousForcedVariations = context.ForcedVariations?.ToDictionary(k => k.Key, v => v.Value);


            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = context.ApiHost ?? "https://cdn.growthbook.io",
                CacheExpirationInSeconds = 60,
                ClientKey = context.ClientKey,
                DecryptionKey = context.DecryptionKey,
                PreferServerSentEvents = true
            };

            // If they didn't want to include a logger factory, just create a basic one that will
            // create disabled loggers by default so we don't force a particular logging provider
            // or logs on the user if they chose the defaults.

            if (context.LoggerFactory != null)
            {
                _loggerFactory = context.LoggerFactory;
                _ownsLoggerFactory = false;
            }
            else
            {
                _loggerFactory = LoggerFactory.Create(builder => { });
                _ownsLoggerFactory = true;
            }

            _logger = _loggerFactory.CreateLogger<GrowthBook>();
            var conditionEvaluatorLogger = _loggerFactory.CreateLogger<ConditionEvaluationProvider>();

            _conditionEvaluator = new ConditionEvaluationProvider(conditionEvaluatorLogger);

            if (context.FeatureRepository != null)
            {
                _featureRepository = context.FeatureRepository;
            }
            else
            {
                var featureCache = new InMemoryFeatureCache(cacheExpirationInSeconds: 60);
                var httpClientFactory = new HttpClientFactory(requestTimeoutInSeconds: 60);

                var featureRefreshLogger = _loggerFactory.CreateLogger<FeatureRefreshWorker>();
                var featureRepositoryLogger = _loggerFactory.CreateLogger<FeatureRepository>();

                var featureRefreshWorker = new FeatureRefreshWorker(featureRefreshLogger, httpClientFactory, config, featureCache);

                IRemoteEvaluationService remoteEvaluationService = null;
                if (context.RemoteEval)
                {
                    var remoteEvaluationLogger = _loggerFactory.CreateLogger<RemoteEvaluationService>();
                    remoteEvaluationService = new RemoteEvaluationService(remoteEvaluationLogger, httpClientFactory);
                }

                _featureRepository = new FeatureRepository(featureRepositoryLogger, featureCache, featureRefreshWorker, remoteEvaluationService);
            }

            HydrateStickyBucketServiceFromContext(context.StickyBucketAssignmentDocs);

            RefreshStickyBucketAssignments();
        }

        /// <summary>
        /// Replaces this instance's sticky bucket assignment docs with what the configured
        /// <see cref="IStickyBucketService"/> currently holds for every hash/fallback attribute in use
        /// across the loaded features and experiments. A no-op when no sticky bucket service is
        /// configured. Called after construction and whenever Features or Attributes change.
        /// </summary>
        private void RefreshStickyBucketAssignments()
        {
            if (_stickyBucketService == null)
            {
                return;
            }

            var sequence = Interlocked.Increment(ref _stickyBucketLoadSequence);

            PublishStickyBucketAssignments(_stickyBucketService.GetAllAssignments(GetStickyBucketAttributeKeys()), sequence);
        }

        /// <summary>
        /// Pulls fresh sticky bucket assignment docs from the configured
        /// <see cref="IAsyncStickyBucketService"/>. A no-op when no async sticky bucket service is
        /// configured.
        /// </summary>
        /// <remarks>
        /// <see cref="LoadFeatures"/> calls this automatically, and so does every attribute change: the async
        /// attribute methods await it, and the synchronous ones dispatch it without waiting, since they have no
        /// way to await an async store. Call it directly only when you need to be sure the refresh has landed
        /// after a synchronous attribute change.
        /// </remarks>
        /// <param name="cancellationToken">Used for monitoring the need to cancel the retrieval.</param>
        public async Task LoadStickyBucketAssignmentsAsync(CancellationToken? cancellationToken = null)
        {
            if (_asyncStickyBucketService == null)
            {
                return;
            }

            var sequence = Interlocked.Increment(ref _stickyBucketLoadSequence);

            var refreshedDocuments = await _asyncStickyBucketService
                .GetAllAssignmentsAsync(GetStickyBucketAttributeKeys(), cancellationToken ?? CancellationToken.None);

            PublishStickyBucketAssignments(refreshedDocuments, sequence);
        }

        /// <summary>
        /// Builds the formatted attribute keys ("name||value") that a sticky bucket store needs documents
        /// for, based on the hash/fallback attributes used across the loaded features and experiments.
        /// </summary>
        private IList<string> GetStickyBucketAttributeKeys()
        {
            return FormatStickyBucketKeys(ExperimentUtilities.DeriveStickyBucketIdentifierAttributes(Features, Experiments));
        }

        /// <summary>
        /// Turns identifier attribute names into the "name||value" keys a sticky bucket store is asked for,
        /// using this instance's current attribute values.
        /// </summary>
        private IList<string> FormatStickyBucketKeys(IEnumerable<string> identifierAttributes)
        {
            var formattedKeys = new List<string>();

            // The constructor assigns the backing field straight from the context, and Dispose clears it, so
            // this can run with no attributes at all. An absent identifier just yields no key to ask for,
            // which is what the reference SDK does too - it reads a missing attribute as an empty value.
            var attributes = Attributes ?? new JObject();

            foreach (var attributeName in identifierAttributes)
            {
                (_, string hashValue) = attributes.GetHashAttributeAndValue(attributeName);

                if (!hashValue.IsNullOrWhitespace())
                {
                    formattedKeys.Add(new StickyAssignmentsDocument(attributeName, hashValue).FormattedAttribute);
                }
            }

            return formattedKeys;
        }

        /// <summary>
        /// Persists an assignment to the asynchronous store. Exceptions are caught and logged rather than
        /// propagated, because this is dispatched without being awaited - an unhandled failure here would
        /// otherwise surface as an unobserved task exception far from its cause.
        /// </summary>
        private async Task SaveStickyBucketAssignmentAsync(StickyAssignmentsDocument document)
        {
            try
            {
                await _asyncStickyBucketService.SaveAssignmentsAsync(document).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save sticky bucket assignments for attribute '{FormattedAttribute}'", document.FormattedAttribute);
            }
        }

        /// <summary>
        /// Refreshes the assignments from an asynchronous store on behalf of a caller that cannot await, which
        /// is every synchronous attribute-change method. Exceptions are caught and logged rather than
        /// propagated, because nothing observes this task.
        /// </summary>
        private async Task RefreshAsyncStickyBucketAssignmentsAsync()
        {
            try
            {
                await LoadStickyBucketAssignmentsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh sticky bucket assignments after an attribute change");
            }
        }

        /// <summary>
        /// Publishes the refreshed assignment docs as the current ones, with a single reference assignment so
        /// that a concurrent evaluation sees either the previous set or the refreshed one, never a partial one.
        /// </summary>
        /// <param name="refreshedDocuments">What the store currently holds. A null value leaves the docs alone.</param>
        /// <param name="sequence">
        /// The position of the load that produced them. A load that started earlier holds the previous
        /// identifier's documents, so it is dropped rather than published over a newer set.
        /// </param>
        private void PublishStickyBucketAssignments(IDictionary<string, StickyAssignmentsDocument> refreshedDocuments, long sequence)
        {
            if (refreshedDocuments == null)
            {
                return;
            }

            lock (_stickyBucketWriteLock)
            {
                if (sequence <= _publishedStickyBucketLoad)
                {
                    _logger.LogDebug("Discarded sticky bucket assignments from load '{Sequence}', already published '{Published}'", sequence, _publishedStickyBucketLoad);

                    return;
                }

                _publishedStickyBucketLoad = sequence;

                // Copied rather than stored directly: the store owns the dictionary it returned and may reuse it.
                _stickyBucketAssignmentDocs = new Dictionary<string, StickyAssignmentsDocument>(refreshedDocuments);
            }
        }

        /// <summary>
        /// Adds a single assignment doc by publishing a replacement dictionary, since writing into the live
        /// one would race with the evaluations reading it.
        /// </summary>
        private void StoreStickyBucketAssignment(StickyAssignmentsDocument document)
        {
            lock (_stickyBucketWriteLock)
            {
                var updated = new Dictionary<string, StickyAssignmentsDocument>(_stickyBucketAssignmentDocs);

                updated[document.FormattedAttribute] = document;

                _stickyBucketAssignmentDocs = updated;
            }
        }

        /// <summary>
        /// Makes sure the current docs cover an experiment handed straight to <see cref="Run"/>, which the
        /// up-front load misses when the experiment is in neither the features nor the context.
        /// </summary>
        /// <remarks>
        /// Synchronous store only. With an asynchronous one, list the experiment on the
        /// <see cref="Context"/> or call <see cref="LoadStickyBucketAssignmentsAsync"/> after adding it.
        /// </remarks>
        private void EnsureStickyBucketAssignmentsFor(Experiment experiment)
        {
            if (_stickyBucketService == null || experiment == null)
            {
                return;
            }

            var identifierAttributes = new HashSet<string> { experiment.HashAttribute ?? "id" };

            if (!string.IsNullOrEmpty(experiment.FallbackAttribute))
            {
                identifierAttributes.Add(experiment.FallbackAttribute);
            }

            var currentDocuments = _stickyBucketAssignmentDocs;
            var missingKeys = new List<string>();

            foreach (var key in FormatStickyBucketKeys(identifierAttributes))
            {
                if (currentDocuments == null || !currentDocuments.ContainsKey(key))
                {
                    missingKeys.Add(key);
                }
            }

            if (missingKeys.Count == 0)
            {
                return;
            }

            var found = _stickyBucketService.GetAllAssignments(missingKeys);

            if (found == null)
            {
                return;
            }

            foreach (var document in found.Values)
            {
                StoreStickyBucketAssignment(document);
            }
        }

        /// <summary>
        /// Writes assignment docs supplied on the Context into the sticky bucket service, so they survive
        /// the full replace done by <see cref="RefreshStickyBucketAssignments"/>. The reference SDK does the
        /// same in its constructor; without it, docs handed in directly would be dropped by the first
        /// refresh unless the caller had separately written them to the store themselves.
        /// </summary>
        private void HydrateStickyBucketServiceFromContext(IDictionary<string, StickyAssignmentsDocument> providedDocuments)
        {
            if (_stickyBucketService == null || providedDocuments == null)
            {
                return;
            }

            foreach (var document in providedDocuments.Values)
            {
                if (document == null)
                {
                    continue;
                }

                try
                {
                    _stickyBucketService.SaveAssignments(document);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to hydrate sticky bucket service with the assignment doc for '{FormattedAttribute}'", document.FormattedAttribute);
                }
            }
        }

        /// <summary>
        /// Arbitrary JSON object containing user and request attributes.
        /// </summary>
        /// <remarks>
        /// Assigning replaces the attributes entirely and starts a remote evaluation in the background when the
        /// change requires one. Use <see cref="UpdateAttributesAsync(object, CancellationToken?)"/> to wait for that
        /// evaluation instead, or <see cref="UpdateAttributes(object)"/> to hand over attributes that this instance
        /// should take a private copy of rather than share with the caller.
        /// </remarks>
        public JObject Attributes
        {
            get => _attributes;
            set => ApplyAttributes(value, isMerge: false);
        }

        /// <summary>
        /// Dictionary of the currently loaded feature objects.
        /// </summary>
        public IDictionary<string, Feature> Features { get; set; }

        /// <summary>
        /// The currently loaded experiments (separate from features).
        /// </summary>
        public IList<Experiment> Experiments { get; set; }

        /// <summary>
        /// Listing of specific experiments to always assign a specific variation (used for QA).
        /// </summary>
        /// <remarks>
        /// Forced variations are part of the remote evaluation payload, so assigning starts a remote evaluation in
        /// the background when the change requires one. Use
        /// <see cref="SetForcedVariationsAsync(IDictionary{string, int}, CancellationToken?)"/> to wait for that
        /// evaluation instead.
        /// </remarks>
        public IDictionary<string, int> ForcedVariations
        {
            get => _forcedVariations;
            set => ApplyForcedVariations(value);
        }

        /// <summary>
        /// The URL of the current page.
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        ///  Switch to globally disable all experiments. Default true.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Helper function used to cleanup object state.
        /// </summary>
        /// <param name="disposing">If true, dispose of large objects.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // Through the backing fields: tearing the instance down must not start a remote evaluation.
                    _attributes = null;
                    _forcedVariations = null;
                    _pendingRemoteEvaluation = null;
                    Features.Clear();
                    _trackingCallback = null;
                    _forcedFeatures.Clear();
                    _assigned.Clear();
                    _tracked.Clear();
                    _subscribers.Clear();
                    _asyncSubscribers.Clear();
                    _featureRepository.Cancel();

                    if (_ownsLoggerFactory && _loggerFactory is IDisposable disposableFactory)
                    {
                        disposableFactory.Dispose();
                    }
                }
                _disposedValue = true;
            }
        }

        /// <summary>
        /// Called to dispose of this object's data.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// GrowthBook function to dispose of object data. Alias for Dispose().
        /// </summary>
        public void Destroy()
        {
            Dispose();
        }

        /// <summary>
        /// Replaces all user attributes with the ones provided.
        /// </summary>
        /// <remarks>
        /// This is a full replace: any attribute that isn't present in <paramref name="attributes"/> is dropped.
        /// Use <see cref="MergeAttributes(IDictionary{string, object})"/> to merge into the existing attributes instead.
        /// Passing null clears all attributes, and a null value is stored as a JSON null rather than removing the key.
        /// </remarks>
        /// <param name="attributes">New user attributes as IDictionary, or null to clear all attributes.</param>
        public void UpdateAttributes(IDictionary<string, object> attributes)
        {
            ApplyAttributes(ToJObject(attributes), isMerge: false);

            _logger?.LogDebug("Replaced attributes with {Count} properties", attributes?.Count ?? 0);
        }

        /// <summary>
        /// Replaces all user attributes with the ones provided.
        /// </summary>
        /// <remarks>
        /// This is a full replace: any attribute that isn't present in <paramref name="attributes"/> is dropped.
        /// Use <see cref="MergeAttributes(object)"/> to merge into the existing attributes instead.
        /// Passing null clears all attributes, and a null value is stored as a JSON null rather than removing the key.
        /// </remarks>
        /// <param name="attributes">New user attributes as an anonymous object or a <see cref="JObject"/>, or null to clear all attributes.</param>
        public void UpdateAttributes(object attributes)
        {
            ApplyAttributes(ToJObject(attributes), isMerge: false);

            _logger?.LogDebug("Replaced attributes from object");
        }

        /// <summary>
        /// Merges additional attributes into the existing ones.
        /// </summary>
        /// <remarks>
        /// This is a shallow merge, matching the TypeScript SDK's updateAttributes(): new keys are added, existing keys
        /// are overwritten, and keys that aren't present in <paramref name="additionalAttributes"/> are preserved.
        /// Nested objects are replaced rather than merged. Passing null is a no-op, and a null value is stored as a
        /// JSON null rather than removing the key.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge</param>
        public void MergeAttributes(IDictionary<string, object> additionalAttributes)
        {
            if (additionalAttributes == null) return;

            ApplyAttributes(ToJObject(additionalAttributes), isMerge: true);

            _logger?.LogDebug("Merged {Count} additional attributes", additionalAttributes.Count);
        }

        /// <summary>
        /// Merges additional attributes into the existing ones.
        /// </summary>
        /// <remarks>
        /// This is a shallow merge, matching the TypeScript SDK's updateAttributes(): new keys are added, existing keys
        /// are overwritten, and keys that aren't present in <paramref name="additionalAttributes"/> are preserved.
        /// Nested objects are replaced rather than merged. Passing null is a no-op, and a null value is stored as a
        /// JSON null rather than removing the key.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge as an anonymous object or a <see cref="JObject"/>.</param>
        public void MergeAttributes(object additionalAttributes)
        {
            if (additionalAttributes == null) return;

            ApplyAttributes(ToJObject(additionalAttributes), isMerge: true);

            _logger?.LogDebug("Merged additional attributes from object");
        }

        /// <summary>
        /// Replaces the forced feature value overrides for this instance.
        /// </summary>
        public void SetForcedFeatures(IDictionary<string, JToken> forcedFeatures)
        {
            if (forcedFeatures != null)
            {
                _forcedFeatures = new Dictionary<string, JToken>(forcedFeatures);
            }
            else
            {
                _forcedFeatures = new Dictionary<string, JToken>();
            }

            _logger?.LogDebug("Set {Count} forced feature override(s)", _forcedFeatures.Count);
        }

        /// <summary>
        /// Replaces all user attributes with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="UpdateAttributes(IDictionary{string, object})"/>, except that the returned task
        /// only completes once any triggered remote evaluation has finished.
        /// </remarks>
        /// <param name="attributes">New user attributes as IDictionary, or null to clear all attributes.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        public Task UpdateAttributesAsync(IDictionary<string, object> attributes, CancellationToken? cancellationToken = null)
        {
            _logger?.LogDebug("Replaced attributes with {Count} properties", attributes?.Count ?? 0);

            return ApplyAttributesAsync(ToJObject(attributes), isMerge: false, cancellationToken);
        }

        /// <summary>
        /// Replaces all user attributes with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="UpdateAttributes(object)"/>, except that the returned task only completes once
        /// any triggered remote evaluation has finished.
        /// </remarks>
        /// <param name="attributes">New user attributes as an anonymous object or a <see cref="JObject"/>, or null to clear all attributes.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        public Task UpdateAttributesAsync(object attributes, CancellationToken? cancellationToken = null)
        {
            _logger?.LogDebug("Replaced attributes from object");

            return ApplyAttributesAsync(ToJObject(attributes), isMerge: false, cancellationToken);
        }

        /// <summary>
        /// Merges additional attributes into the existing ones and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="MergeAttributes(IDictionary{string, object})"/>, except that the returned task
        /// only completes once any triggered remote evaluation has finished.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the merge and any remote evaluation it triggered.</returns>
        public Task MergeAttributesAsync(IDictionary<string, object> additionalAttributes, CancellationToken? cancellationToken = null)
        {
            if (additionalAttributes == null) return Task.CompletedTask;

            _logger?.LogDebug("Merged {Count} additional attributes", additionalAttributes.Count);

            return ApplyAttributesAsync(ToJObject(additionalAttributes), isMerge: true, cancellationToken);
        }

        /// <summary>
        /// Merges additional attributes into the existing ones and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="MergeAttributes(object)"/>, except that the returned task only completes once
        /// any triggered remote evaluation has finished.
        /// </remarks>
        /// <param name="additionalAttributes">Additional attributes to merge as an anonymous object or a <see cref="JObject"/>.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the merge and any remote evaluation it triggered.</returns>
        public Task MergeAttributesAsync(object additionalAttributes, CancellationToken? cancellationToken = null)
        {
            if (additionalAttributes == null) return Task.CompletedTask;

            _logger?.LogDebug("Merged additional attributes from object");

            return ApplyAttributesAsync(ToJObject(additionalAttributes), isMerge: true, cancellationToken);
        }

        /// <summary>
        /// Replaces the forced variations with the ones provided.
        /// </summary>
        /// <remarks>
        /// Forced variations are part of the remote evaluation payload, so this starts a remote evaluation in the
        /// background when the change requires one. Equivalent to assigning <see cref="ForcedVariations"/>.
        /// </remarks>
        /// <param name="forcedVariations">The experiment keys to force to a specific variation, or null to clear them.</param>
        public void SetForcedVariations(IDictionary<string, int> forcedVariations)
        {
            _logger?.LogDebug("Replaced forced variations with {Count} entries", forcedVariations?.Count ?? 0);

            ApplyForcedVariations(forcedVariations);
        }

        /// <summary>
        /// Replaces the forced variations with the ones provided and, in remote evaluation mode, waits for the
        /// features to be evaluated again against them.
        /// </summary>
        /// <remarks>
        /// Behaves like <see cref="SetForcedVariations(IDictionary{string, int})"/>, except that the returned task
        /// only completes once any triggered remote evaluation has finished.
        /// </remarks>
        /// <param name="forcedVariations">The experiment keys to force to a specific variation, or null to clear them.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the update and any remote evaluation it triggered.</returns>
        public Task SetForcedVariationsAsync(IDictionary<string, int> forcedVariations, CancellationToken? cancellationToken = null)
        {
            _logger?.LogDebug("Replaced forced variations with {Count} entries", forcedVariations?.Count ?? 0);

            if (!SwapInForcedVariations(forcedVariations))
            {
                return Task.CompletedTask;
            }

            return StartRemoteEvaluation(cancellationToken);
        }

        /// <summary>
        /// Publishes the provided forced variations and starts a remote evaluation in the background if the change
        /// requires one.
        /// </summary>
        /// <param name="forcedVariations">The forced variations to apply.</param>
        private void ApplyForcedVariations(IDictionary<string, int> forcedVariations)
        {
            if (SwapInForcedVariations(forcedVariations))
            {
                // The caller has no way to wait for this, so the task is kept around for the next feature load
                // to await. Callers that need the refreshed features can use SetForcedVariationsAsync.
                StartRemoteEvaluation(null);
            }
        }

        /// <summary>
        /// Applies the provided attributes, either replacing the existing ones entirely or merging into them,
        /// and starts a remote evaluation in the background if the change requires one.
        /// </summary>
        /// <param name="attributes">The attributes to apply.</param>
        /// <param name="isMerge">True to merge into the existing attributes, false to replace them.</param>
        private void ApplyAttributes(JObject attributes, bool isMerge)
        {
            var requiresRemoteEvaluation = SwapInAttributes(attributes, isMerge);

            // Outside the attributes lock, since this reaches the sticky bucket service. Every attribute change
            // funnels through here - including a direct assignment to Attributes - so the assignments are
            // re-resolved for the new identifier exactly once per change.
            RefreshStickyBucketAssignments();

            if (_asyncStickyBucketService != null)
            {
                // An asynchronous store can't be read from here, so the refresh is dispatched the same way
                // writes to that store are. It lands shortly after this returns; callers that need it to have
                // completed before they evaluate should use the async overloads, which await it.
                _ = RefreshAsyncStickyBucketAssignmentsAsync();
            }

            if (requiresRemoteEvaluation)
            {
                // The caller has no way to wait for this, so the task is kept around for the next feature load
                // to await. Callers that need the refreshed features can use the async version of this method.
                StartRemoteEvaluation(null);
            }
        }

        /// <summary>
        /// Applies the provided attributes, either replacing the existing ones entirely or merging into them,
        /// and waits for any remote evaluation the change requires.
        /// </summary>
        /// <param name="attributes">The attributes to apply.</param>
        /// <param name="isMerge">True to merge into the existing attributes, false to replace them.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>
        /// A <see cref="Task"/> that represents the sticky bucket refresh and any remote evaluation that was
        /// triggered.
        /// </returns>
        private async Task ApplyAttributesAsync(JObject attributes, bool isMerge, CancellationToken? cancellationToken)
        {
            var requiresRemoteEvaluation = SwapInAttributes(attributes, isMerge);

            RefreshStickyBucketAssignments();

            // The reference SDK's setAttributes awaits refreshStickyBuckets, so an attribute change always
            // re-resolves the assignments for the new identifier. The synchronous overloads can't await an
            // async store, but these can, so here both store flavors are treated alike.
            await LoadStickyBucketAssignmentsAsync(cancellationToken).ConfigureAwait(false);

            if (!requiresRemoteEvaluation)
            {
                return;
            }

            await StartRemoteEvaluation(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a remote evaluation and records it so that a subsequent feature load can wait for it.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A <see cref="Task"/> that represents the remote evaluation.</returns>
        private Task StartRemoteEvaluation(CancellationToken? cancellationToken)
        {
            // Attributes and forced variations are part of the remote evaluation payload, so changing them makes
            // the previously evaluated features stale. This runs after the swap so the request uses the new state.
            var evaluationContext = CreateRemoteEvaluationContext(out var generation);
            var remoteEvaluation = TriggerRemoteEvaluationAsync(evaluationContext, generation, cancellationToken);

            Interlocked.Exchange(ref _pendingRemoteEvaluation, remoteEvaluation);

            return remoteEvaluation;
        }

        /// <summary>
        /// Snapshots the state a remote evaluation request is built from and allocates the generation that
        /// identifies that request.
        /// </summary>
        /// <remarks>
        /// Both happen under the same lock on purpose. Requests are independent, so a slower earlier one can
        /// complete after a newer one, and only the generation tells them apart. Allocating it while holding
        /// the lock that publishes the state makes the generation order the same as the order the state was
        /// read in, so the highest generation is always the one carrying the newest state. Allocating it
        /// outside the lock would reintroduce the very race it exists to close.
        /// </remarks>
        /// <param name="generation">The generation identifying the request built from the returned state.</param>
        /// <returns>The state the remote evaluation must run against.</returns>
        private Context CreateRemoteEvaluationContext(out long generation)
        {
            lock (_attributesLock)
            {
                generation = Interlocked.Increment(ref _remoteEvaluationGeneration);

                return CreateCurrentContext();
            }
        }

        /// <summary>
        /// Determines whether the remote evaluation identified by the provided generation is still the most
        /// recent one, and may therefore publish its features.
        /// </summary>
        /// <param name="generation">The generation of the remote evaluation to check.</param>
        /// <returns>True if no newer remote evaluation has been started since.</returns>
        private bool IsLatestRemoteEvaluation(long generation)
        {
            return Interlocked.Read(ref _remoteEvaluationGeneration) == generation;
        }

        /// <summary>
        /// Builds the updated attributes and publishes them as the current ones.
        /// </summary>
        /// <param name="attributes">The attributes to apply.</param>
        /// <param name="isMerge">True to merge into the existing attributes, false to replace them.</param>
        /// <returns>True if the change requires a remote evaluation.</returns>
        private bool SwapInAttributes(JObject attributes, bool isMerge)
        {
            lock (_attributesLock)
            {
                var updatedAttributes = attributes;

                if (isMerge)
                {
                    updatedAttributes = _attributes?.DeepClone() as JObject ?? new JObject();

                    foreach (var property in attributes.Properties())
                    {
                        updatedAttributes[property.Name] = property.Value;
                    }
                }

                var shouldTriggerRemoteEvaluation = ShouldTriggerRemoteEvaluation(updatedAttributes);

                // The updated attributes are built off to the side and swapped in with a single reference assignment
                // so that a concurrent evaluation sees either the previous attributes or the fully updated ones,
                // but never a partially merged state.
                _attributes = updatedAttributes;

                SnapshotRemoteEvaluationState(updatedAttributes);

                return shouldTriggerRemoteEvaluation;
            }
        }

        /// <summary>
        /// Publishes the provided forced variations as the current ones.
        /// </summary>
        /// <param name="forcedVariations">The forced variations to apply.</param>
        /// <returns>True if the change requires a remote evaluation.</returns>
        private bool SwapInForcedVariations(IDictionary<string, int> forcedVariations)
        {
            lock (_attributesLock)
            {
                // Published before the comparison so that ShouldTriggerRemoteEvaluation sees the new forced
                // variations against the previous snapshot, the same way the attributes path works.
                _forcedVariations = forcedVariations;

                var shouldTriggerRemoteEvaluation = ShouldTriggerRemoteEvaluation(_attributes);

                SnapshotRemoteEvaluationState(_attributes);

                return shouldTriggerRemoteEvaluation;
            }
        }

        /// <summary>
        /// Records the current attributes and forced variations as the state the last remote evaluation was made for.
        /// </summary>
        /// <remarks>
        /// Callers must hold <see cref="_attributesLock"/>. Both are snapshotted together because they're compared
        /// together: snapshotting only the attributes would leave the forced variations permanently different from
        /// the previous ones once they've been changed, so every later change would trigger a remote evaluation
        /// whether or not it needed one.
        /// </remarks>
        /// <param name="attributes">The attributes that are now current.</param>
        private void SnapshotRemoteEvaluationState(JObject attributes)
        {
            // Only ShouldTriggerRemoteEvaluation reads these, and it answers false outright without remote evaluation.
            // Skipping the clone keeps assigning attributes as cheap as it was for everyone evaluating locally.
            if (!_context.RemoteEval)
            {
                return;
            }

            _previousAttributes = attributes?.DeepClone() as JObject;
            _previousForcedVariations = _forcedVariations?.ToDictionary(k => k.Key, v => v.Value);
        }

        /// <summary>
        /// Converts the provided attributes into a JSON object that this instance can safely take ownership of.
        /// </summary>
        /// <param name="attributes">The attributes to convert, which may be null.</param>
        /// <returns>The attributes as a JSON object, or an empty one if they were null.</returns>
        private static JObject ToJObject(object attributes)
        {
            if (attributes == null)
            {
                return new JObject();
            }

            // Clone rather than take the caller's instance so that later changes on their side
            // can't mutate the attributes that evaluations are running against.
            if (attributes is JObject json)
            {
                return (JObject)json.DeepClone();
            }

            return JObject.FromObject(attributes);
        }

        /// <inheritdoc />
        public bool IsOn(string key)
        {
            return EvalFeature(key).On;
        }

        /// <inheritdoc />
        public bool IsOff(string key)
        {
            return EvalFeature(key).Off;
        }

        /// <summary>
        /// Asynchronously checks whether the specified feature is enabled.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns><c>true</c> if the feature is on; otherwise, <c>false</c>.</returns>
        public async Task<bool> IsOnAsync(string key, CancellationToken? cancellationToken = null)
        {
            await LoadFeatures(cancellationToken: cancellationToken);
            var result = EvaluateFeature(key);
            var value = result.Value;
            return !value.IsNull() && value.ToObject<bool>();
        }

        /// <summary>
        /// Asynchronously checks whether the specified feature is disabled.
        /// </summary>
        /// <param name="key">The feature key.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns><c>true</c> if the feature is off; otherwise, <c>false</c>.</returns>
        public async Task<bool> IsOffAsync(string key, CancellationToken? cancellationToken = null)
        {
            var on = await IsOnAsync(key, cancellationToken);
            return !on;
        }

        /// <summary>
        /// Subscribes a synchronous callback to experiment/feature evaluations.
        /// </summary>
        public IDisposable Subscribe(Action<Experiment, ExperimentResult> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _subscribers.Add(callback);
            return new Subscription(() => _subscribers.Remove(callback));
        }

        /// <summary>
        /// Subscribes an asynchronous callback to experiment/feature evaluations.
        /// </summary>
        public IDisposable SubscribeAsync(Func<Experiment, ExperimentResult, Task> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            _asyncSubscribers.Add(callback);
            return new Subscription(() => _asyncSubscribers.Remove(callback));
        }

        private class Subscription : IDisposable
        {
            private readonly Action _unsubscribe;
            private bool _disposed;

            public Subscription(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _unsubscribe();
                    _disposed = true;
                }
            }
        }



        /// <inheritdoc />
        public T GetFeatureValue<T>(string key, T fallback, bool alwaysLoadFeatures = false)
        {
            // Keep the sync API, but avoid deadlocks by doing a quick synchronous spin only if already completed.
            // Prefer callers to use the async APIs.
            if (alwaysLoadFeatures)
            {
                // Fire-and-wait carefully to avoid deadlocks.
                LoadFeatures().GetAwaiter().GetResult();
            }

            var result = EvaluateFeature(key);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public async Task<T> GetFeatureValueAsync<T>(string key, T fallback, CancellationToken? cancellationToken = null)
        {
            var result = await EvalFeatureAsync(key, cancellationToken);
            var value = result.Value;

            return value.IsNull() ? fallback : value.ToObject<T>();
        }

        /// <inheritdoc />
        public IDictionary<string, ExperimentAssignment> GetAllResults()
        {
            return _assigned;
        }

        /// <inheritdoc />
        public FeatureResult EvalFeature(string featureId, bool alwaysLoadFeatures = false)
        {
            if (alwaysLoadFeatures)
            {
                LoadFeatures().GetAwaiter().GetResult();
            }

            return EvaluateFeature(featureId);
        }

        public async Task<FeatureResult> EvalFeatureAsync(string featureId, CancellationToken? cancellationToken = null)
        {
            await LoadFeatures(cancellationToken: cancellationToken);

            return EvaluateFeature(featureId);
        }

        private FeatureResult EvaluateFeature(string featureId, ISet<string> evaluatedFeatures = default)
        {
            try
            {
                evaluatedFeatures = evaluatedFeatures ?? new HashSet<string>();

                if (evaluatedFeatures.Contains(featureId))
                {
                    return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                }

                evaluatedFeatures.Add(featureId);

                if (_forcedFeatures.TryGetValue(featureId, out JToken forcedValue))
                {
                    _logger.LogDebug("Feature '{FeatureId}' has a forced override, returning it without evaluating rules", featureId);
                    return GetFeatureResult(forcedValue ?? JValue.CreateNull(), FeatureResult.SourceId.Override);
                }

                if (!Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                _logger.LogDebug("Evaluating feature '{FeatureId}' with {RuleCount} rules", featureId, feature?.Rules?.Count ?? 0);

                var ruleIndex = 0;

                foreach (FeatureRule rule in feature?.Rules ?? Enumerable.Empty<FeatureRule>())
                {
                    ruleIndex++;
                    if (rule.ParentConditions != null)
                    {
                        var passedPrerequisiteEvaluations = true;

                        foreach (var parentCondition in rule.ParentConditions)
                        {
                            // Use a fresh copy of the evaluated feature ids to avoid
                            // incorrectly flagging repeated prerequisite evaluations as cycles
                            var parentResult = EvaluateFeature(parentCondition.Id, new HashSet<string>(evaluatedFeatures));

                            // Don't continue evaluating if the prerequisite conditions have cycles.
                            if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                            {
                                _logger.LogWarning("Detected cyclic prerequisite while evaluating parent feature '{ParentId}' for feature '{FeatureId}'. Evaluated: {EvaluatedFeatures}", parentCondition.Id, featureId, string.Join(",", evaluatedFeatures));
                                return GetFeatureResult(default, FeatureResult.SourceId.CyclicPrerequisite);
                            }

                            var evaluationObject = new JObject { ["value"] = parentResult.Value };

                            var isSuccess = _conditionEvaluator.EvalCondition(evaluationObject, parentCondition.Condition ?? new JObject(), _savedGroups);

                            if (!isSuccess)
                            {
                                // When the parent evaluation is gated we'll treat that as a complete failure.

                                if (parentCondition.Gate)
                                {
                                    _logger.LogDebug("Rule {RuleIndex}: Gated prerequisite '{ParentId}' failed for feature '{FeatureId}', aborting", ruleIndex, parentCondition.Id, featureId);
                                    return GetFeatureResult(default, FeatureResult.SourceId.Prerequisite);
                                }

                                passedPrerequisiteEvaluations = false;
                                _logger.LogDebug("Rule {RuleIndex}: Prerequisite '{ParentId}' did not pass for feature '{FeatureId}', continuing to next rule", ruleIndex, parentCondition.Id, featureId);
                                break;
                            }
                        }

                        if (!passedPrerequisiteEvaluations)
                        {
                            continue;
                        }
                    }

                    if (rule.Filters?.Any() == true && IsFilteredOut(rule.Filters))
                    {
                        continue;
                    }

                    if (!rule.Condition.IsNull() && !_conditionEvaluator.EvalCondition(Attributes, rule.Condition, _savedGroups))
                    {
                        _logger.LogDebug("Rule {RuleIndex}: attribute condition did not match, continuing", ruleIndex);
                        continue;
                    }

                    if (!rule.Force.IsNull())
                    {
                        if (!IsIncludedInRollout(rule.Seed ?? featureId, rule.HashAttribute, rule.Range, rule.Coverage, rule.HashVersion))
                        {
                            _logger.LogDebug("Rule {RuleIndex}: excluded by rollout/coverage, continuing", ruleIndex);
                            continue;
                        }

                        if (_trackingCallback != null && rule.Tracks?.Any() == true)
                        {
                            foreach (var trackData in rule.Tracks)
                            {
                                try
                                {
                                    _trackingCallback?.Invoke(trackData.Experiment, trackData.Result);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, $"Encountered unhandled exception in tracking callback for feature ID '{featureId}'");
                                }
                            }
                        }

                        NotifySubscribers(null, new ExperimentResult
                        {
                            InExperiment = false,
                            Value = rule.Force
                        });

                        _logger.LogDebug("Rule {RuleIndex}: returning forced value for feature '{FeatureId}'", ruleIndex, featureId);
                        return GetFeatureResult(rule.Force, FeatureResult.SourceId.Force, ruleId: rule.Id);
                    }

                    var experiment = new Experiment
                    {
                        Variations = rule.Variations,
                        Key = rule.Key ?? featureId,
                        Coverage = rule.Coverage,
                        Weights = rule.Weights,
                        HashAttribute = rule.HashAttribute,
                        FallbackAttribute = rule.FallbackAttribute,
                        DisableStickyBucketing = rule.DisableStickyBucketing,
                        BucketVersion = rule.BucketVersion,
                        MinBucketVersion = rule.MinBucketVersion,
                        Namespace = rule.Namespace,
                        Meta = rule.Meta,
                        Ranges = rule.Ranges,
                        Name = rule.Name,
                        Phase = rule.Phase,
                        Seed = rule.Seed,
                        Filters = rule.Filters,
                        HashVersion = rule.HashVersion,
                        Condition = rule.Condition
                    };

                    var result = RunExperiment(experiment, featureId);

                    TryAssignExperimentResult(experiment, result);

                    if (!result.InExperiment || result.Passthrough)
                    {
                        continue;
                    }

                    NotifySubscribers(experiment, result);

                    return GetFeatureResult(result.Value, FeatureResult.SourceId.Experiment, experiment, result, ruleId: rule.Id);
                }

                _logger.LogDebug("No rules matched for feature '{FeatureId}', returning default value", featureId);
                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Encountered an unhandled exception while executing '{nameof(EvalFeature)}'");

                if (!Features.TryGetValue(featureId, out Feature feature))
                {
                    return GetFeatureResult(null, FeatureResult.SourceId.UnknownFeature);
                }

                return GetFeatureResult(feature.DefaultValue ?? null, FeatureResult.SourceId.DefaultValue);
            }
        }

        /// <inheritdoc />
        public ExperimentResult Run(Experiment experiment)
        {
            try
            {
                EnsureStickyBucketAssignmentsFor(experiment);

                ExperimentResult result = RunExperiment(experiment, null);

                TryAssignExperimentResult(experiment, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Encountered an unhandled exception while executing '{nameof(Run)}'");

                return null;
            }
        }

        /// <inheritdoc />
        public async Task LoadFeatures(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            var result = await LoadFeaturesWithResult(options, cancellationToken);

            if (!result.Success)
            {
                // For backward compatibility, we still throw exceptions in the original LoadFeatures method
                // Users who want better error handling should use LoadFeaturesWithResult
                throw result.Exception ?? new GrowthBookException(result.ErrorMessage);
            }
        }

        /// <inheritdoc />
        public async Task<FeatureLoadResult> LoadFeaturesWithResult(GrowthBookRetrievalOptions options = null, CancellationToken? cancellationToken = null)
        {
            try
            {
                _logger.LogInformation("Loading features from the repository");
                IDictionary<string, Feature> features;

                // A remote evaluation started by an attribute change may still be in flight. Wait for it first so
                // that its now older response can't land after this load and overwrite the features it retrieves.
                // It handles its own errors, so it never faults.
                var pendingRemoteEvaluation = Interlocked.Exchange(ref _pendingRemoteEvaluation, null);

                if (pendingRemoteEvaluation != null)
                {
                    await pendingRemoteEvaluation;
                }

                // Use remote evaluation if enabled and configured
                var isRemoteEvaluation = _context.RemoteEval && RemoteEvaluationUtilities.IsValidForRemoteEvaluation(_context);
                var generation = 0L;

                if (isRemoteEvaluation)
                {
                    var evaluationContext = CreateRemoteEvaluationContext(out generation);
                    features = await _featureRepository.GetFeaturesWithContext(evaluationContext, options, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    features = await _featureRepository.GetFeatures(options, cancellationToken);
                }

                if (features == null)
                {
                    var errorMessage = "Feature repository returned null - no features were loaded";
                    _logger.LogWarning(errorMessage);
                    return FeatureLoadResult.CreateFailure(errorMessage);
                }

                // An attribute change during this fetch started a newer remote evaluation, so these features
                // were evaluated for state that has already been replaced. The newer evaluation owns what gets
                // applied; this load reports what is currently applied rather than overwriting it.
                if (isRemoteEvaluation && !IsLatestRemoteEvaluation(generation))
                {
                    _logger.LogDebug("Discarding a superseded remote evaluation response received while loading features");

                    return FeatureLoadResult.CreateSuccess(Features?.Count ?? 0);
                }

                Features = features;
                var featureCount = Features.Count;

                _logger.LogInformation($"Loading features has completed, retrieved '{featureCount}' features");

                RefreshStickyBucketAssignments();
                await LoadStickyBucketAssignmentsAsync(cancellationToken);

                return FeatureLoadResult.CreateSuccess(featureCount);
            }
            catch (FeatureLoadException ex)
            {
                var errorMessage = $"Failed to load features: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Keep Features as is (don't set to null) to avoid NullReferenceExceptions
                return FeatureLoadResult.CreateFailure(errorMessage, ex, ex.StatusCode);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Encountered an unhandled exception while loading features: {ex.Message}";
                _logger.LogError(ex, errorMessage);

                // Keep Features as is (don't set to null) to avoid NullReferenceExceptions
                return FeatureLoadResult.CreateFailure(errorMessage, ex);
            }
        }

        private void TryAssignExperimentResult(Experiment experiment, ExperimentResult result)
        {
            var assignment = new ExperimentAssignment { Experiment = experiment, Result = result };
            bool shouldFireCallbacks = false;

            // Always record the assignment locally for GetAllResults()
            if (!_assigned.TryGetValue(experiment.Key, out ExperimentAssignment prev)
                || prev.Result.InExperiment != result.InExperiment
                || prev.Result.VariationId != result.VariationId)
            {
                _assigned[experiment.Key] = assignment;
                shouldFireCallbacks = true;
            }

            // Also use repository tracking if available (for preventing duplicate callbacks across instances)
            if (_featureRepository != null)
            {
                if (!_featureRepository.HasIdenticalAssignment(experiment.Key, assignment))
                {
                    _featureRepository.RecordAssignment(experiment.Key, assignment);
                }
            }

            // Fire subscription callbacks if needed
            if (shouldFireCallbacks)
            {
                NotifySubscribers(experiment, result);
            }
        }

        private ExperimentResult RunExperiment(Experiment experiment, string featureId)
        {
            // 1. Abort if there aren't enough variations present.

            if (experiment.Variations.IsNull() || experiment.Variations.Count < 2)
            {
                _logger.LogDebug("Aborting experiment, not enough variations are present");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 2. Abort if GrowthBook is currently disabled.

            if (!Enabled)
            {
                _logger.LogDebug("Aborting experiment, GrowthBook is not currently enabled");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // NOTE: The improved URL targeting mentioned is only applicable on the front end.
            //       There are potential frontend usages for the C# SDK, but until there is more clarity and more robust tests
            //       in the JSON test suite to ensure we get an appropriate implementation in place we are going to hold off on this.

            // 2.6 Use improved URL targeting if specified.

            //if (experiment.UrlPatterns?.Count > 0 && !ExperimentUtilities.IsUrlTargeted(Url ?? string.Empty, experiment.UrlPatterns))
            //{
            //    _logger.LogDebug("Skipping due to URL targeting");
            //    return GetExperimentResult(experiment, featureId: featureId);
            //}

            // 3. Use the override value from the query string if one is specified.

            if (!Url.IsNullOrWhitespace())
            {
                var overrideValue = ExperimentUtilities.GetQueryStringOverride(experiment.Key, Url, experiment.Variations.Count);

                if (overrideValue != null)
                {
                    _logger.LogDebug("Found an override value in the query string, creating experiment result from it");
                    return GetExperimentResult(experiment, overrideValue.Value, featureId: featureId);
                }
            }

            // 4. Use the forced variation value instead if one is specified for this experiment.

            if (ForcedVariations.TryGetValue(experiment.Key, out var variation))
            {
                _logger.LogDebug("Found a forced variation value, creating experiment result from it");
                return GetExperimentResult(experiment, variation, featureId: featureId);
            }

            // 5. Abort if the experiment isn't currently active.

            if (!experiment.Active)
            {
                _logger.LogDebug("Aborting experiment, experiment is not currently active");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 6. Abort if we're unable to generate a hash identifying this run.

            (var hashAttribute, var hashValue) = Attributes.GetHashAttributeAndValue(experiment.HashAttribute);

            if (hashValue.IsNullOrWhitespace())
            {
                // Check if a fallback attribute for sticky bucketing exists and use it if possible.

                var hasFallback = !experiment.FallbackAttribute.IsNullOrWhitespace();

                if (hasFallback)
                {
                    (hashAttribute, hashValue) = Attributes.GetHashAttributeAndValue(experiment.FallbackAttribute);
                }
                else
                {
                    _logger.LogDebug("Aborting experiment, unable to locate a value for the experiment hash attribute \'{ExperimentHashAttribute}\'", experiment.HashAttribute);
                    return GetExperimentResult(experiment, featureId: featureId);
                }
            }

            // 6.5 When sticky bucketing is permitted, determine if they already have a value and use it if possible.

            var assignedBucket = -1;
            var foundStickyBucket = false;
            var stickyBucketVersionIsBlocked = false;

            if (IsStickyBucketingEnabled && !experiment.DisableStickyBucketing)
            {
                var bucketVersion = experiment.BucketVersion;
                var minBucketVersion = experiment.MinBucketVersion;
                var meta = experiment.Meta ?? new List<VariationMeta>();

                var stickyBucketVariation = ExperimentUtilities.GetStickyBucketVariation(
                    experiment,
                    bucketVersion,
                    minBucketVersion,
                    meta,
                    Attributes,
                    _stickyBucketAssignmentDocs
                );

                foundStickyBucket = stickyBucketVariation.VariationIndex >= 0;
                assignedBucket = stickyBucketVariation.VariationIndex;
                stickyBucketVersionIsBlocked = stickyBucketVariation.IsVersionBlocked;
            }

            if (!foundStickyBucket)
            {
                // 7. Abort if this run is ineligible to be included in the experiment.

                if (experiment.Filters?.Any() == true)
                {
                    if (IsFilteredOut(experiment.Filters))
                    {
                        _logger.LogDebug("Aborting experiment, filters have been applied and matched this run");
                        return GetExperimentResult(experiment, featureId: featureId);
                    }
                }
                else if (experiment.Namespace != null && !ExperimentUtilities.InNamespace(hashValue, experiment.Namespace))
                {
                    _logger.LogDebug("Aborting experiment, not within the specified namespace \'{ExperimentNamespace}\'", experiment.Namespace);
                    return GetExperimentResult(experiment, featureId: featureId);
                }

                // 8. Abort if the conditions for the experiment prohibit this.

                if (!experiment.Condition.IsNull())
                {
                    if (!_conditionEvaluator.EvalCondition(Attributes, experiment.Condition, _savedGroups))
                    {
                        _logger.LogDebug("Aborting experiment, associated conditions have prohibited participation");
                        return GetExperimentResult(experiment, featureId: featureId);
                    }
                }

                if (experiment.ParentConditions != null)
                {
                    foreach (var parentCondition in experiment.ParentConditions)
                    {
                        // Use a fresh copy of the evaluated feature ids to avoid
                        // incorrectly flagging repeated prerequisite evaluations as cycles
                        var parentResult = EvaluateFeature(parentCondition.Id, new HashSet<string>());

                        if (parentResult.Source == FeatureResult.SourceId.CyclicPrerequisite)
                        {
                            return GetExperimentResult(experiment, featureId: featureId);
                        }

                        var evaluationObject = new JObject { ["value"] = parentResult.Value };

                        if (!_conditionEvaluator.EvalCondition(evaluationObject, parentCondition.Condition ?? new JObject(), _savedGroups))
                        {
                            return GetExperimentResult(experiment, featureId: featureId);
                        }
                    }
                }
            }

            // 9. Attempt to assign this run to an experiment variation.

            var hash = HashUtilities.Hash(experiment.Seed ?? experiment.Key, hashValue, experiment.HashVersion);

            if (hash is null)
            {
                return GetExperimentResult(experiment, featureId: featureId);
            }

            if (!foundStickyBucket)
            {
                var ranges = experiment.Ranges?.Count > 0 ? experiment.Ranges : ExperimentUtilities.GetBucketRanges(experiment.Variations?.Count ?? 0, experiment.Coverage ?? 1, experiment.Weights ?? new List<double>());
                assignedBucket = ExperimentUtilities.ChooseVariation(hash.Value, ranges.ToList());

                // 10. Abort if a variation could not be assigned.

                if (assignedBucket == -1)
                {
                    _logger.LogDebug("Aborting experiment, unable to assign this run to an experiment variation");
                    return GetExperimentResult(experiment, featureId: featureId);
                }
            }

            // 9.5 Unenroll if any prior sticky buckets are blocked by version.

            if (stickyBucketVersionIsBlocked)
            {
                return GetExperimentResult(experiment, featureId: featureId, wasStickyBucketUsed: true);
            }

            // 11. Use the forced value for the experiment if one is specified.

            if (experiment.Force != null)
            {
                _logger.LogDebug("Found a forced value, creating experiment result from it");
                return GetExperimentResult(experiment, experiment.Force.Value, featureId: featureId);
            }

            // 12. Abort if we're currently operating in QA mode.

            if (_qaMode)
            {
                _logger.LogDebug("Aborting experiment, this run is in QA mode");
                return GetExperimentResult(experiment, featureId: featureId);
            }

            // 13. Run the experiment and track the result if we haven't seen this one before.

            _logger.LogInformation("Participation in experiment with key \'{ExperimentKey}\' is allowed, running the experiment", experiment.Key);
            var result = GetExperimentResult(experiment, assignedBucket, true, featureId, hash, foundStickyBucket);

            // 13.5 Store the value for later if sticky bucketing is enabled.

            if (IsStickyBucketingEnabled && !experiment.DisableStickyBucketing)
            {
                var experimentKey = ExperimentUtilities.GetStickyBucketExperimentKey(experiment.Key, experiment.BucketVersion);

                var assignments = new Dictionary<string, string>
                {
                    [experimentKey] = result.Key
                };

                StickyAssignmentsDocument document;
                bool isChanged;

                if (_stickyBucketService != null)
                {
                    (document, isChanged) = ExperimentUtilities.GenerateStickyBucketAssignment(_stickyBucketService, hashAttribute, hashValue, assignments);
                }
                else
                {
                    var formattedAttribute = new StickyAssignmentsDocument(hashAttribute, hashValue).FormattedAttribute;
                    _stickyBucketAssignmentDocs.TryGetValue(formattedAttribute, out var existingDocument);

                    (document, isChanged) = ExperimentUtilities.GenerateStickyBucketAssignment(existingDocument, hashAttribute, hashValue, assignments);
                }

                if (isChanged)
                {
                    StoreStickyBucketAssignment(document);

                    if (_stickyBucketService != null)
                    {
                        _stickyBucketService.SaveAssignments(document);
                    }
                    else
                    {
                        _ = SaveStickyBucketAssignmentAsync(document);
                    }
                }
            }

            TryToTrack(experiment, result);

            return result;
        }

        private FeatureResult GetFeatureResult(JToken value, string source, Experiment experiment = null, ExperimentResult experimentResult = null, string ruleId = null)
        {
            return new FeatureResult
            {
                Value = value,
                Source = source,
                Experiment = experiment,
                ExperimentResult = experimentResult,
                RuleId = ruleId ?? string.Empty
            };
        }

        private bool IsFilteredOut(IEnumerable<Filter> filters)
        {
            foreach (var filter in filters)
            {
                (_, var hashValue) = Attributes.GetHashAttributeAndValue(filter.Attribute);

                if (hashValue.IsNullOrWhitespace())
                {
                    _logger.LogDebug("Attributes are missing a filter\'s hash attribute of \'{FilterAttribute}\', marking as filtered out", filter.Attribute);
                    return true;
                }

                var bucket = HashUtilities.Hash(filter.Seed, hashValue, filter.HashVersion);

                var isInAnyRange = filter.Ranges.Any(x => ExperimentUtilities.InRange(bucket.Value, x));

                if (!isInAnyRange)
                {
                    _logger.LogDebug("This run is not in any range associated with a filter, marking as filtered out");
                    return true;
                }
            }

            return false;
        }

        private bool IsIncludedInRollout(string seed, string hashAttribute = null, BucketRange range = null, double? coverage = null, int? hashVersion = null)
        {
            if (coverage == null && range == null)
            {
                _logger.LogDebug("No coverage value or range was specified, marking as included in rollout");
                return true;
            }

            if (range is null && coverage == 0)
            {
                _logger.LogDebug("Range and coverage were not set, marking as not included in rollout");
                return false;
            }

            (_, var hashValue) = Attributes.GetHashAttributeAndValue(hashAttribute);

            if (hashValue is null)
            {
                _logger.LogDebug("Attributes do not have a value for hash attribute \'{HashAttribute}\', marking as excluded from rollout", hashAttribute);
                return false;
            }

            var bucket = HashUtilities.Hash(seed, hashValue, hashVersion ?? 1);

            if (range != null)
            {
                return ExperimentUtilities.InRange(bucket.Value, range);
            }

            if (coverage != null)
            {
                return bucket <= coverage;
            }

            return true;
        }

        /// <summary>
        /// Generates an experiment result from an experiment.
        /// </summary>
        /// <param name="experiment">The experiment to get the result from.</param>
        /// <param name="variationIndex">The variation id, if specified.</param>
        /// <param name="hashUsed">Whether or not a hash was used in assignment.</param>
        /// <returns>The experiment result.</returns>
        private ExperimentResult GetExperimentResult(Experiment experiment, int variationIndex = -1, bool hashUsed = false, string featureId = null, double? bucketHash = null, bool wasStickyBucketUsed = false)
        {
            var inExperiment = true;

            if (variationIndex < 0 || variationIndex >= experiment.Variations.Count)
            {
                variationIndex = 0;
                inExperiment = false;
            }

            var canUseStickyBucketing = IsStickyBucketingEnabled && !experiment.DisableStickyBucketing;
            var fallbackAttribute = canUseStickyBucketing ? experiment.FallbackAttribute : default;

            (var hashAttribute, var hashValue) = Attributes.GetHashAttributeAndValue(experiment.HashAttribute, fallbackAttributeKey: fallbackAttribute);

            var meta = experiment.Meta?.Count > 0 ? experiment.Meta[variationIndex] : null;

            var result = new ExperimentResult
            {
                Key = meta?.Key ?? variationIndex.ToString(),
                FeatureId = featureId,
                InExperiment = inExperiment,
                HashAttribute = hashAttribute,
                HashUsed = hashUsed,
                HashValue = hashValue,
                Value = experiment.Variations is null ? null : experiment.Variations[variationIndex],
                VariationId = variationIndex,
                Name = meta?.Name,
                Passthrough = meta?.Passthrough ?? false,
                Bucket = bucketHash ?? 0d,
                StickyBucketUsed = wasStickyBucketUsed
            };

            result.Name = meta?.Name;
            result.Passthrough = meta?.Passthrough ?? false;
            result.Bucket = bucketHash ?? 0d;

            return result;
        }

        /// <summary>
        /// Calls the tracking callback function to track experiment assignment.
        /// </summary>
        /// <param name="experiment">The experiment that was assigned.</param>
        /// <param name="result">The result of the assignment.</param>
        private void TryToTrack(Experiment experiment, ExperimentResult result)
        {
            if (_trackingCallback == null)
            {
                return;
            }

            string key = result.HashAttribute + result.HashValue + experiment.Key + result.VariationId;

            // Use atomic operations to prevent race conditions in concurrent scenarios
            bool shouldTrack = false;

            if (_featureRepository != null)
            {
                // TryMarkAsTracked returns true only if key was successfully added (didn't exist before)
                shouldTrack = _featureRepository.TryMarkAsTracked(key);
            }
            else
            {
                // TryAdd returns true only if key was successfully added (didn't exist before)
                shouldTrack = _tracked.TryAdd(key, 0);
            }

            if (shouldTrack)
            {
                try
                {
                    _trackingCallback(experiment, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Encountered unhandled exception during tracking callback for experiment with combined key \'{Key}\'", key);
                }
            }
        }

         /// <summary>
        /// Validates that remote evaluation configuration is correct.
        /// </summary>
        /// <param name="context">The context to validate</param>
        private static void ValidateRemoteEvaluationConfiguration(Context context)
        {
            if (!context.RemoteEval) return;

            if (string.IsNullOrWhiteSpace(context.ClientKey))
            {
                throw new ArgumentException("ClientKey is required when RemoteEval is enabled", nameof(context));
            }

            if (string.IsNullOrWhiteSpace(context.ApiHost))
            {
                throw new ArgumentException("ApiHost is required when RemoteEval is enabled", nameof(context));
            }

            if (!string.IsNullOrWhiteSpace(context.DecryptionKey))
            {
                throw new ArgumentException("RemoteEval cannot be used with DecryptionKey - features are evaluated server-side", nameof(context));
            }
        }

        /// <summary>
        /// Validates that only one sticky bucket service is configured. Allowing both would leave it
        /// ambiguous which store owns an assignment, and writes would silently go to only one of them.
        /// </summary>
        /// <param name="context">The context to validate</param>
        private static void ValidateStickyBucketConfiguration(Context context)
        {
            if (context.StickyBucketService != null && context.AsyncStickyBucketService != null)
            {
                throw new ArgumentException("StickyBucketService and AsyncStickyBucketService cannot both be set - choose the one that matches your backing store", nameof(context));
            }
        }

        /// <summary>
        /// Determines if remote evaluation should be triggered based on attribute or forced variation changes.
        /// </summary>
        /// <param name="newAttributes">The new attributes to check</param>
        /// <returns>True if remote evaluation should be triggered</returns>
        private bool ShouldTriggerRemoteEvaluation(JObject newAttributes)
        {
            // Nothing is evaluated remotely, so no change can make a remote evaluation stale.
            if (!_context.RemoteEval)
            {
                return false;
            }

            // Check if attributes changed
            var attributesChanged = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluation(
                _previousAttributes,
                newAttributes,
                _context.CacheKeyAttributes
            );

            // Check if forced variations changed
            var forcedVariationsChanged = RemoteEvaluationUtilities.ShouldTriggerRemoteEvaluationForForcedVariations(
                _previousForcedVariations,
                _forcedVariations
            );

            return attributesChanged || forcedVariationsChanged;
        }

        /// <summary>
        /// Triggers remote evaluation asynchronously when a change to the evaluated state is detected.
        /// </summary>
        /// <param name="evaluationContext">The state to evaluate against, snapshotted when the generation was allocated.</param>
        /// <param name="generation">The generation identifying this remote evaluation.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        private async Task TriggerRemoteEvaluationAsync(Context evaluationContext, long generation, CancellationToken? cancellationToken)
        {
            try
            {
                _logger?.LogDebug("Triggering remote evaluation due to attribute or forced variation changes");

                var features = await _featureRepository.GetFeaturesWithContext(evaluationContext, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (features == null)
                {
                    return;
                }

                // The atomic swap keeps the state consistent, but the features derived from it are published here,
                // and this response may well have overtaken a newer one. Publishing it would leave the caller on
                // features evaluated for state they've already replaced, so a superseded response is dropped whole
                // rather than applied.
                if (!IsLatestRemoteEvaluation(generation))
                {
                    _logger?.LogDebug("Discarding a superseded remote evaluation response with {Count} features", features.Count);
                    return;
                }

                Features = features;
                _logger?.LogDebug("Remote evaluation completed, updated {Count} features", features.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to trigger remote evaluation, continuing with cached features");
            }
        }

        /// <summary>
        /// Creates a context object with current state for remote evaluation.
        /// </summary>
        /// <returns>A context object with current state</returns>
        private Context CreateCurrentContext()
        {
            return new Context
            {
                RemoteEval = _context.RemoteEval,
                ApiHost = _context.ApiHost,
                ClientKey = _context.ClientKey,
                CacheKeyAttributes = _context.CacheKeyAttributes,
                Attributes = Attributes,
                ForcedVariations = ForcedVariations,
                Url = Url
            };
        }


        /// <summary>
        /// Notifies all synchronous and asynchronous subscribers about a feature or experiment evaluation result.
        /// </summary>
        /// <param name="experiment">The experiment that was evaluated (null if feature evaluation).</param>
        /// <param name="result">The result of the evaluation.</param>
        private void NotifySubscribers(Experiment experiment, ExperimentResult result)
        {
            foreach (var subscriber in _subscribers)
            {
                try
                {
                    subscriber(experiment, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Encountered unhandled exception in synchronous subscriber.");
                }
            }

            foreach (var asyncSubscriber in _asyncSubscribers)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await asyncSubscriber(experiment, result).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Encountered unhandled exception in asynchronous subscriber.");
                    }
                });
            }
        }
    }
}
