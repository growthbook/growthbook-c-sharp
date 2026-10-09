using System;
using System.Collections.Generic;
using GrowthBook.Api;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Factory for creating GrowthBook instances with shared configuration and repository.
    /// Register this as a Singleton in DI — it owns one shared <see cref="IGrowthBookFeatureRepository"/>
    /// (and its background refresh worker) that all per-user <see cref="GrowthBook"/> instances reuse.
    /// </summary>
    [Obsolete("GrowthBookFactory is deprecated. Use GrowthBookClient (GrowthBook.MultiUser) instead, which provides a cleaner multiuser API with stateless evaluators.")]
    public class GrowthBookFactory : IDisposable
    {
        private readonly Context _baseContext;
        private readonly IGrowthBookFeatureRepository _sharedRepository;
        private readonly bool _ownsSharedRepository;
        private readonly ILoggerFactory _loggerFactory;
        private readonly bool _ownsLoggerFactory;
        private bool _disposed = false;

        /// <summary>
        /// Creates a new GrowthBookFactory with base configuration.
        /// If <paramref name="baseContext"/> has a <see cref="Context.FeatureRepository"/> set,
        /// it is used as-is (caller owns its lifetime). Otherwise, if <see cref="Context.ClientKey"/>
        /// is set, a shared repository is created internally using <see cref="Context.FeatureCache"/>
        /// (or <see cref="InMemoryFeatureCache"/> by default) so that all instances share one worker.
        /// </summary>
        /// <param name="baseContext">Base context containing shared configuration</param>
        public GrowthBookFactory(Context baseContext)
        {
            _baseContext = baseContext?.Clone() ?? throw new ArgumentNullException(nameof(baseContext));

            if (baseContext.FeatureRepository != null)
            {
                _sharedRepository = baseContext.FeatureRepository;
                _ownsSharedRepository = false;
            }
            else if (!string.IsNullOrEmpty(baseContext.ClientKey))
            {
                _loggerFactory = baseContext.LoggerFactory;

                if (_loggerFactory == null)
                {
                    _loggerFactory = LoggerFactory.Create(_ => { });
                    _ownsLoggerFactory = true;
                }

                _sharedRepository = CreateSharedRepository(baseContext, _loggerFactory);
                _ownsSharedRepository = true;
            }
        }

        /// <summary>
        /// Creates a GrowthBook instance for a specific user with IDictionary attributes.
        /// </summary>
        /// <param name="userAttributes">User-specific attributes</param>
        /// <param name="trackingCallback">Optional tracking callback for this user context</param>
        /// <returns>New GrowthBook instance with user context</returns>
        public GrowthBook CreateForUser(IDictionary<string, object> userAttributes, Action<Experiment, ExperimentResult> trackingCallback = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GrowthBookFactory));

            var context = _baseContext.Clone();

            if (userAttributes != null)
            {
                foreach (var kvp in userAttributes)
                {
                    context.Attributes[kvp.Key] = JToken.FromObject(kvp.Value);
                }
            }

            if (_sharedRepository != null)
            {
                context.FeatureRepository = _sharedRepository;
            }

            // Only override when the caller actually supplied one. Assigning unconditionally wiped the base
            // context's callback for every user created without their own, which silently stopped tracking
            // their experiment exposures.
            if (trackingCallback != null)
            {
                context.TrackingCallback = trackingCallback;
            }

            return new GrowthBook(context);
        }

        /// <summary>
        /// Creates a GrowthBook instance for a specific user with anonymous object attributes.
        /// </summary>
        /// <param name="userAttributes">User-specific attributes as anonymous object</param>
        /// <param name="trackingCallback">Optional tracking callback for this user context</param>
        /// <returns>New GrowthBook instance with user context</returns>
        public GrowthBook CreateForUser(object userAttributes, Action<Experiment, ExperimentResult> trackingCallback = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GrowthBookFactory));

            var context = _baseContext.Clone();

            if (userAttributes != null)
            {
                var additionalJObject = JObject.FromObject(userAttributes);
                foreach (var property in additionalJObject.Properties())
                {
                    context.Attributes[property.Name] = property.Value;
                }
            }

            if (_sharedRepository != null)
            {
                context.FeatureRepository = _sharedRepository;
            }

            // Only override when the caller actually supplied one. Assigning unconditionally wiped the base
            // context's callback for every user created without their own, which silently stopped tracking
            // their experiment exposures.
            if (trackingCallback != null)
            {
                context.TrackingCallback = trackingCallback;
            }

            return new GrowthBook(context);
        }

        /// <summary>
        /// Disposes of factory resources. Cancels the shared repository worker, and disposes the logger factory,
        /// if they were created internally (i.e. the caller did not inject their own
        /// <see cref="IGrowthBookFeatureRepository"/> or <see cref="Context.LoggerFactory"/>) — anything supplied
        /// through the base context belongs to the caller, who may still be using it.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ownsSharedRepository)
            {
                _sharedRepository?.Cancel();
            }

            if (_ownsLoggerFactory)
            {
                _loggerFactory.Dispose();
            }
        }

        internal static IGrowthBookFeatureRepository CreateSharedRepository(Context context, ILoggerFactory loggerFactory)
        {
            var config = CreateSharedConfiguration(context);

            var cache = context.FeatureCache ?? new InMemoryFeatureCache(cacheExpirationInSeconds: context.CacheExpirationInSeconds);
            var httpClientFactory = new HttpClientFactory(requestTimeoutInSeconds: context.HttpRequestTimeoutInSeconds);
            var refreshWorker = new FeatureRefreshWorker(
                loggerFactory.CreateLogger<FeatureRefreshWorker>(),
                httpClientFactory,
                config,
                cache);

            // The repository the GrowthBook constructor builds for itself gets one of these whenever the context
            // asks for remote evaluation. Leaving it out here meant the same context evaluated remotely through
            // new GrowthBook(context) but silently did not through the factory.
            IRemoteEvaluationService remoteEvaluationService = null;

            if (context.RemoteEval)
            {
                remoteEvaluationService = new RemoteEvaluationService(
                    loggerFactory.CreateLogger<RemoteEvaluationService>(),
                    httpClientFactory);
            }

            return new FeatureRepository(
                loggerFactory.CreateLogger<FeatureRepository>(),
                cache,
                refreshWorker,
                remoteEvaluationService);
        }

        /// <summary>
        /// Builds the configuration used by the internally created shared repository. Split out from
        /// <see cref="CreateSharedRepository"/> so it can be asserted on directly - the values otherwise
        /// disappear into a private field of <see cref="FeatureRefreshWorker"/>, which is how
        /// <see cref="Context.BackgroundSync"/> came to be hardcoded to true here in the first place.
        /// </summary>
        internal static GrowthBookConfigurationOptions CreateSharedConfiguration(Context context) =>
            new GrowthBookConfigurationOptions
            {
                ApiHost = context.ApiHost ?? "https://cdn.growthbook.io",
                CacheExpirationInSeconds = context.CacheExpirationInSeconds,
                ClientKey = context.ClientKey,
                DecryptionKey = context.DecryptionKey,
                PreferServerSentEvents = context.BackgroundSync,
                // Copied rather than shared. The caller still holds these dictionaries, and the shared worker
                // reads them on every request from a background thread: handing over the same instance would
                // let a later edit change the headers for everyone, or tear a request mid-enumeration.
                RequestHeaders = CopyHeaders(context.RequestHeaders),
                StreamingRequestHeaders = CopyHeaders(context.StreamingRequestHeaders),
                OnFeaturesRefreshed = context.OnFeaturesRefreshed,
                OnStreamingEventId = context.OnStreamingEventId
            };

        private static IDictionary<string, string> CopyHeaders(IDictionary<string, string> headers) =>
            headers is null ? new Dictionary<string, string>() : new Dictionary<string, string>(headers);
    }
}
