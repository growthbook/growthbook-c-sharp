using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Factory for creating GrowthBook instances with shared configuration and repository.
    /// </summary>
    /// <remarks>
    /// Superseded by <see cref="MultiUser.GrowthBookClient"/>, which answers for a user passed to each
    /// call instead of building an instance per user. This factory still works and is not going away in
    /// this release, but it carries the problems that come with one instance per user: a context cloned
    /// for each of them, their plugins and repository shared without that being stated anywhere, and
    /// teardown that has to decide what it owns.
    /// </remarks>
    [Obsolete("Use GrowthBookClient instead. GrowthBookFactory creates one instance per user; GrowthBookClient evaluates for a user passed to each call.")]
    public class GrowthBookFactory : IDisposable
    {
        private readonly Context _baseContext;
        private readonly IGrowthBookFeatureRepository _sharedRepository;
        private bool _disposed = false;

        /// <summary>
        /// Creates a new GrowthBookFactory with base configuration.
        /// </summary>
        /// <param name="baseContext">Base context containing shared configuration</param>
        public GrowthBookFactory(Context baseContext)
        {
            _baseContext = baseContext?.Clone() ?? throw new ArgumentNullException(nameof(baseContext));
            _sharedRepository = baseContext.FeatureRepository;
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
        /// Disposes of factory resources.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
        }
    }
}