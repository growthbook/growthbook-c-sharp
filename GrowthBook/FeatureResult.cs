using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;

namespace GrowthBook {
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class FeatureResult {
        /// <summary>
        /// The assigned value of the feature.
        /// </summary>
        public JToken Value { get; set; }

        /// <summary>
        /// The assigned value cast to a boolean.
        /// </summary>
        public bool On {
            get {
                if (Value == null || Value.Type == JTokenType.Null) {
                    return false;
                }
                string strValue = Value.ToString();
                return !string.IsNullOrEmpty(strValue) && strValue != "0" && strValue.ToLower() != "false";
            }
        }

        /// <summary>
        /// The assigned value cast to a boolean and then negated.
        /// </summary>
        public bool Off { get { return !On; } }

        /// <summary>
        /// One of "unknownFeature", "defaultValue", "force", or "experiment".
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// When source is "experiment", this will be an Experiment object.
        /// </summary>
        public Experiment Experiment { get; set; }

        /// <summary>
        /// When source is "experiment", this will be an ExperimentResult object.
        /// </summary>
        public ExperimentResult ExperimentResult { get; set; }

        /// <summary>
        /// Returns the value of the feature cast to the specified type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>The value of the feature cast to the specified type.</returns>
        public T GetValue<T>() {
            return Value.ToObject<T>();
        }

        public override bool Equals(object obj) {
            if (obj.GetType() == typeof(FeatureResult)) {
                FeatureResult objResult = (FeatureResult)obj;
                return object.Equals(Experiment, objResult.Experiment)
                    && object.Equals(ExperimentResult, objResult.ExperimentResult)
                    && Off == objResult.Off
                    && On == objResult.On
                    && Source == objResult.Source
                    && JToken.DeepEquals(Value ?? JValue.CreateNull(), objResult.Value ?? JValue.CreateNull());
            }
            return false;
        }

        public override int GetHashCode() {
            throw new NotImplementedException();
        }
    }
}