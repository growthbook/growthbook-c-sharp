using System;
using GrowthBook.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class FeatureResult
    {
        /// <summary>
        /// Represents possible values for the source of a feature result.
        /// </summary>
        public static class SourceId
        {
            public const string UnknownFeature = "unknownFeature";
            public const string DefaultValue = "defaultValue";
            public const string Force = "force";
            public const string Experiment = "experiment";
            public const string CyclicPrerequisite = "cyclicPrerequisite";
            public const string Prerequisite = "prerequisite";
        }

        /// <summary>
        /// The assigned value of the feature.
        /// </summary>
        public JToken Value { get; set; }

        /// <summary>
        /// The assigned value cast to a boolean.
        /// </summary>
        public bool On
        {
            get
            {
                // JavaScript truthiness: only null, false, 0, NaN and "" are off
                switch (Value?.Type)
                {
                    case null:
                    case JTokenType.Null:
                    case JTokenType.Undefined:
                        return false;
                    case JTokenType.Boolean:
                        return Value.Value<bool>();
                    case JTokenType.Integer:
                        return Value.ToString() != "0";
                    case JTokenType.Float:
                        var number = Value.Value<double>();
                        return number != 0 && !double.IsNaN(number);
                    case JTokenType.String:
                        return Value.ToString().Length > 0;
                    default:
                        return true;
                }
            }
        }

        /// <summary>
        /// The assigned value cast to a boolean and then negated.
        /// </summary>
        public bool Off { get { return !On; } }

        /// <summary>
        /// One of "unknownFeature", "defaultValue", "force", "experiment", or "cyclicPrerequisite".
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
        public T GetValue<T>()
        {
            return Value.ToObject<T>();
        }

        public override bool Equals(object obj)
        {
            if (obj.GetType() == typeof(FeatureResult))
            {
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

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
