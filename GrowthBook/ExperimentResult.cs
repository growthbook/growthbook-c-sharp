using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace GrowthBook
{
    /// <summary>
    /// The result of running an experiment given a specific context.
    /// </summary>
    public class ExperimentResult
    {
        /// <summary>
        /// Whether or not the user is part of the experiment.
        /// </summary>
        public bool InExperiment { get; set; }

        /// <summary>
        /// The array index of the assigned variation.
        /// </summary>
        public int VariationId { get; set; }

        /// <summary>
        /// The array value of the assigned variation.
        /// </summary>
        public JsonNode? Value { get; set; } = JsonValue.Create((string?)null);

        /// <summary>
        /// If a hash was used to assign a variation.
        /// </summary>
        public bool HashUsed { get; set; }

        /// <summary>
        /// The user attribute used to assign a variation.
        /// </summary>
        public string? HashAttribute { get; set; } = string.Empty;

        /// <summary>
        /// The value of that attribute.
        /// </summary>
        public string? HashValue { get; set; } = string.Empty;

        /// <summary>
        /// The id of the feature (if any) that the experiment came from.
        /// </summary>
        public string? FeatureId { get; set; }

        /// <summary>
        /// The unique key for the assigned variation.
        /// </summary>
        public string? Key { get; set; }

        /// <summary>
        /// The hash value used to assign a variation (float from 0 to 1).
        /// </summary>
        public double Bucket { get; set; }

        /// <summary>
        /// The human-readable name of the assigned variation.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Used for holdout groups.
        /// </summary>
        public bool Passthrough { get; set; }

        /// <summary>
        /// If sticky bucketing was used to assign a variation.
        /// </summary>
        public bool StickyBucketUsed { get; set; }

        /// <summary>
        /// Returns the value of the assigned variation cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The value of the assigned variation cast to the specified type.</returns>
        public T? GetValue<T>()
        {
            if (Value == null)
                return default;

            var typeInfo = GrowthBookJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfo == null)
                return default;

            return (T?)Value.Deserialize((JsonTypeInfo<T>)typeInfo);
        }

        public override bool Equals(object? obj)
        {
            if (obj is ExperimentResult objResult)
            {
                return InExperiment == objResult.InExperiment
                    && HashAttribute == objResult.HashAttribute
                    && HashUsed == objResult.HashUsed
                    && HashValue == objResult.HashValue
                    && JsonNode.DeepEquals(Value, objResult.Value)
                    && VariationId == objResult.VariationId;
            }
            return false;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
