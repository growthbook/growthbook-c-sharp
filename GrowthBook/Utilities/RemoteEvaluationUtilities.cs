using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;


namespace GrowthBook.Utilities
{
    /// <summary>
    /// Utility class for remote evaluation operations including cache key generation.
    /// </summary>
    public static class RemoteEvaluationUtilities
    {
        /// <summary>
        /// Generates a cache key for remote evaluation based on relevant attributes.
        /// Only includes attributes specified in cacheKeyAttributes, or all attributes if not specified.
        /// </summary>
        /// <param name="context">The GrowthBook context</param>
        /// <returns>A cache key string for remote evaluation</returns>
        public static string? GenerateCacheKey(Context context)
        {
            if (context == null || !context.RemoteEval)
                return null;

            // Base key components
            var baseKey = $"{context.ApiHost?.TrimEnd('/')}||{context.ClientKey}";

            // Get relevant attributes for cache key
            var relevantAttributes = GetRelevantAttributes(context.Attributes, context.CacheKeyAttributes);

            // Get forced variations
            var forcedVariations = context.ForcedVariations ?? new Dictionary<string, int>();

            // Create cache context
            var cacheContext = new
            {
                attributes = relevantAttributes,
                forcedVariations = forcedVariations.Count > 0 ? forcedVariations : null,
                url = !string.IsNullOrEmpty(context.Url) ? context.Url : null
            };
            var typeInfo = GrowthBookJsonContext.Default.GetTypeInfo(typeof(object));
            var cacheContextJson = JsonSerializer.Serialize(cacheContext, typeInfo!);

            return $"{baseKey}||{cacheContextJson}";
        }

        /// <summary>
        /// Determines if remote evaluation should be triggered based on attribute changes.
        /// </summary>
        /// <param name="oldAttributes">Previous attributes</param>
        /// <param name="newAttributes">New attributes</param>
        /// <param name="cacheKeyAttributes">Attributes to monitor for changes</param>
        /// <returns>True if remote evaluation should be triggered</returns>
        public static bool ShouldTriggerRemoteEvaluation(
            JsonObject? oldAttributes,
            JsonObject? newAttributes,
            string[]? cacheKeyAttributes)
        {
            if (oldAttributes == null && newAttributes == null)
                return false;

            if (oldAttributes == null || newAttributes == null)
                return true;

            // If no cache key attributes specified, monitor all attributes
            if (cacheKeyAttributes == null || cacheKeyAttributes.Length == 0)
            {
                return !JsonNode.DeepEquals(oldAttributes, newAttributes);
            }

            // Check only specified cache key attributes for changes
            foreach (var attributeKey in cacheKeyAttributes)
            {
                var oldValue = oldAttributes[attributeKey];
                var newValue = newAttributes[attributeKey];

                if (!JsonNode.DeepEquals(oldValue, newValue))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if remote evaluation should be triggered based on forced variations changes.
        /// </summary>
        /// <param name="oldForcedVariations">Previous forced variations</param>
        /// <param name="newForcedVariations">New forced variations</param>
        /// <returns>True if remote evaluation should be triggered</returns>
        public static bool ShouldTriggerRemoteEvaluationForForcedVariations(
            IDictionary<string, int>? oldForcedVariations,
            IDictionary<string, int>? newForcedVariations)
        {
            if (oldForcedVariations == null && newForcedVariations == null)
                return false;

            if (oldForcedVariations == null || newForcedVariations == null)
                return true;

            if (oldForcedVariations.Count != newForcedVariations.Count)
                return true;

            foreach (var kvp in oldForcedVariations)
            {
                if (!newForcedVariations.TryGetValue(kvp.Key, out var newValue) || newValue != kvp.Value)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets relevant attributes based on cache key attributes filter.
        /// </summary>
        /// <param name="allAttributes">All available attributes</param>
        /// <param name="cacheKeyAttributes">Attributes to include in cache key</param>
        /// <returns>Filtered attributes object</returns>
        private static JsonObject GetRelevantAttributes(JsonObject? allAttributes, string[]? cacheKeyAttributes)
        {
            if (allAttributes == null)
                return new JsonObject();

            // If no cache key attributes specified, use all attributes
            if (cacheKeyAttributes == null || cacheKeyAttributes.Length == 0)
                return (allAttributes.DeepClone() as JsonObject)!;

            // Filter to only include specified cache key attributes
            var relevantAttributes = new JsonObject();
            foreach (var attributeKey in cacheKeyAttributes)
            {
                if (allAttributes.ContainsKey(attributeKey))
                {
                    var value = allAttributes[attributeKey];
                    if (value != null)
                    {
                        relevantAttributes[attributeKey] = value.DeepClone();
                    }
                }
            }

            return relevantAttributes;
        }

        /// <summary>
        /// Validates that the context is properly configured for remote evaluation.
        /// </summary>
        /// <param name="context">The context to validate</param>
        /// <returns>True if valid for remote evaluation</returns>
        public static bool IsValidForRemoteEvaluation(Context context)
        {
            if (context == null || !context.RemoteEval)
                return false;

            if (string.IsNullOrWhiteSpace(context.ClientKey))
                return false;

            if (string.IsNullOrWhiteSpace(context.ApiHost))
                return false;

            if (!string.IsNullOrWhiteSpace(context.DecryptionKey))
                return false;

            return true;
        }
    }
}
