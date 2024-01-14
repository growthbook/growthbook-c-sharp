using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// The result of running an experiment given a specific context.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
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
        public JToken Value { get; set; } = JValue.CreateNull();

        /// <summary>
        /// If a hash was used to assign a variation.
        /// </summary>
        public bool HashUsed { get; set; }

        /// <summary>
        /// The user attribute used to assign a variation.
        /// </summary>
        public string HashAttribute { get; set; } = string.Empty;

        /// <summary>
        /// The value of that attribute.
        /// </summary>
        public string HashValue { get; set; } = string.Empty;

        /// <summary>
        /// The id of the feature (if any) that the experiment came from.
        /// </summary>
        public string FeatureId { get; set; }

        // TODO: Set the key to variation's array index if experiment.meta is not set or incomplete.

        /// <summary>
        /// The unique key for the assigned variation.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The hash value used to assign a variation (float from 0 to 1).
        /// </summary>
        public float Bucket { get; set; }

        /// <summary>
        /// The human-readable name of the assigned variation.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Used for holdout groups.
        /// </summary>
        public bool Passthrough { get; set; }

        /// <summary>
        /// Returns the value of the assigned variation cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The value of the assigned variation cast to the specified type.</returns>
        public T GetValue<T>()
        {
            return Value.ToObject<T>();
        }

        public override bool Equals(object obj)
        {
            if (obj.GetType() == typeof(ExperimentResult))
            {
                ExperimentResult objResult = (ExperimentResult)obj;
                return InExperiment == objResult.InExperiment
                    && HashAttribute == objResult.HashAttribute
                    && HashUsed == objResult.HashUsed
                    && HashValue == objResult.HashValue
                    && JToken.DeepEquals(Value ?? JValue.CreateNull(), objResult.Value ?? JValue.CreateNull())
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
