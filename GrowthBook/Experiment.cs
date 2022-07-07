using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GrowthBook {
    /// <summary>
    /// Represents a single experiment with multiple variations.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class Experiment {
        /// <summary>
        /// The globally unique identifier for the experiment.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The different variations to choose between.
        /// </summary>
        public JArray Variations { get; set; }

        /// <summary>
        /// How to weight traffic between variations. Must add to 1.
        /// </summary>
        public IList<double> Weights { get; set; }

        /// <summary>
        /// If set to false, always return the control (first variation).
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// What percent of users should be included in the experiment (between 0 and 1, inclusive).
        /// </summary>
        public double Coverage { get; set; } = 1;

        /// <summary>
        /// Optional targeting condition.
        /// </summary>
        public JObject Condition { get; set; }

        /// <summary>
        /// Adds the experiment to a namespace.
        /// </summary>
        public Namespace Namespace { get; set; }

        /// <summary>
        /// All users included in the experiment will be forced into the specific variation index.
        /// </summary>
        public int? Force { get; set; }

        /// <summary>
        /// What user attribute should be used to assign variations (defaults to id).
        /// </summary>
        public string HashAttribute { get; set; } = "id";

        /// <summary>
        /// Returns the experiment variations cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The variations cast as the specified type.</returns>
        public T GetVariations<T>() {
            return Variations.ToObject<T>();
        }

        public override bool Equals(object obj) {
            if (obj.GetType() == typeof(Experiment)) {
                Experiment objExp = (Experiment)obj;
                return Active == objExp.Active
                    && JToken.DeepEquals(Condition, objExp.Condition)
                    && Coverage == objExp.Coverage
                    && Force == objExp.Force
                    && HashAttribute == objExp.HashAttribute
                    && Key == objExp.Key
                    && object.Equals(Namespace, objExp.Namespace)
                    && JToken.DeepEquals(Variations, objExp.Variations)
                    && ((Weights == null && objExp.Weights == null) || Weights.SequenceEqual(objExp.Weights));
            }
            return false;
        }

        public override int GetHashCode() {
            throw new NotImplementedException();
        }
    }
}