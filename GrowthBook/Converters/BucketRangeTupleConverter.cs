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

                if (array.Count == 2 && IsNumber(array[0]) && IsNumber(array[1]))
                {
                    return new BucketRange(array[0].Value<double>(), array[1].Value<double>());
                }
            }

            return null;
        }

        private static bool IsNumber(JToken token) => token.Type == JTokenType.Integer || token.Type == JTokenType.Float;

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var bucketRange = (BucketRange)value;

            var array = new JArray
            {
                JToken.FromObject(bucketRange.Start),
                JToken.FromObject(bucketRange.End)
            };

            array.WriteTo(writer);
        }
    }
}
