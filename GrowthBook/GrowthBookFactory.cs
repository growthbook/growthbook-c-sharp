using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GrowthBook
{
    /// <summary>
    /// Factory for creating GrowthBook instances with shared configuration and repository.
    /// </summary>
    public class GrowthBookFactory : IDisposable
    {
        private readonly Context _baseContext;
        private readonly IGrowthBookFeatureRepository? _sharedRepository;
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
        public GrowthBook CreateForUser(IDictionary<string, object> userAttributes, Action<Experiment?, ExperimentResult?>? trackingCallback = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GrowthBookFactory));

            var context = _baseContext.Clone();
            context.Attributes ??= new JsonObject();

            if (userAttributes != null)
            {
                foreach (var kvp in userAttributes)
                {
                    JsonNode? node = JsonSerializer.SerializeToNode(
                kvp.Value,
                GrowthBookJsonContext.Default.Object // Use the generated JsonTypeInfo for object
            );

                    if (node != null)
                    {
                        context.Attributes[kvp.Key] = node;
                    }
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
        public GrowthBook CreateForUser(object userAttributes, Action<Experiment?, ExperimentResult?>? trackingCallback = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GrowthBookFactory));

            var context = _baseContext.Clone();
            context.Attributes ??= new JsonObject();

            if (userAttributes != null)
            {
                var additionalObject = Context.ToJsonObject(userAttributes);

                foreach (var property in additionalObject)
                {
                    context.Attributes[property.Key] = property.Value?.DeepClone();
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