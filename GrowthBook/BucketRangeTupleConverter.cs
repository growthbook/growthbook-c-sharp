using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Represents a JsonConverter object used to convert BucketRanges
    /// to and from JSON tuples.
    /// </summary>
    public class BucketRangeTupleConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(BucketRange);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JToken token = JToken.Load(reader);
            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;

                if (float.TryParse(array[0].ToString(), out var start) && float.TryParse(array[1].ToString(), out var end))
                {
                    return new BucketRange(start, end);
                }
            }
            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Namespace valueNamespace = (Namespace)value;
            JArray t = new JArray
            {
                JToken.FromObject(valueNamespace.Id),
                JToken.FromObject(valueNamespace.Start),
                JToken.FromObject(valueNamespace.End)
            };
            t.WriteTo(writer);
        }
    }
}
