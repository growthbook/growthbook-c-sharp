using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;

namespace GrowthBook {
    /// <summary>
    /// The result of running an experiment given a specific context.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ExperimentResult {
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
        public JToken Value { get; set; }

        /// <summary>
        /// If a hash was used to assign a variation.
        /// </summary>
        public bool HashUsed { get; set; }

        /// <summary>
        /// The user attribute used to assign a variation.
        /// </summary>
        public string HashAttribute { get; set; }

        /// <summary>
        /// The value of that attribute.
        /// </summary>
        public string HashValue { get; set; }

        /// <summary>
        /// Returns the value of the assigned variation cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The value of the assigned variation cast to the specified type.</returns>
        public T GetValue<T>() {
            return Value.ToObject<T>();
        }

        public override bool Equals(object obj) {
            if (obj.GetType() == typeof(ExperimentResult)) {
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

        public override int GetHashCode() {
            throw new NotImplementedException();
        }
    }
}