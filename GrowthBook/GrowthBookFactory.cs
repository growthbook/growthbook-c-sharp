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
    public class GrowthBookFactory : IDisposable
    {
        private readonly Context _baseContext;
        private readonly IGrowthBookFeatureRepository _sharedRepository;
        private readonly bool _ownsSharedRepository;
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
                _sharedRepository = CreateSharedRepository(baseContext);
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

            context.TrackingCallback = trackingCallback;

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

            context.TrackingCallback = trackingCallback;

            return new GrowthBook(context);
        }

        /// <summary>
        /// Disposes of factory resources. Cancels the shared repository worker if it was
        /// created internally (i.e. caller did not inject their own <see cref="IGrowthBookFeatureRepository"/>).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_ownsSharedRepository)
            {
                _sharedRepository?.Cancel();
            }
        }

        private static IGrowthBookFeatureRepository CreateSharedRepository(Context context)
        {
            var loggerFactory = context.LoggerFactory ?? LoggerFactory.Create(_ => { });

            var config = new GrowthBookConfigurationOptions
            {
                ApiHost = context.ApiHost ?? "https://cdn.growthbook.io",
                CacheExpirationInSeconds = 60,
                ClientKey = context.ClientKey,
                DecryptionKey = context.DecryptionKey,
                PreferServerSentEvents = true
            };

            var cache = context.FeatureCache ?? new InMemoryFeatureCache(cacheExpirationInSeconds: 60);
            var httpClientFactory = new HttpClientFactory(requestTimeoutInSeconds: 60);
            var refreshWorker = new FeatureRefreshWorker(
                loggerFactory.CreateLogger<FeatureRefreshWorker>(),
                httpClientFactory,
                config,
                cache);

            return new FeatureRepository(
                loggerFactory.CreateLogger<FeatureRepository>(),
                cache,
                refreshWorker);
        }
    }
}
