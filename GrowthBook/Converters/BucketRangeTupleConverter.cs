using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Converters
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
            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;

                if (double.TryParse(array[0].ToString(), out var start) && double.TryParse(array[1].ToString(), out var end))
                {
                    return new BucketRange(start, end);
                }
            }

            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var valueNamespace = (Namespace)value;

            var array = new JArray
            {
                JToken.FromObject(valueNamespace.Id),
                JToken.FromObject(valueNamespace.Start),
                JToken.FromObject(valueNamespace.End)
            };

            array.WriteTo(writer);
        }
    }
}
