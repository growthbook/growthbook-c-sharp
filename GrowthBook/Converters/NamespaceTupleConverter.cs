using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Converters
{
    /// <summary>
    /// Represents a JsonConverter object used to convert Namespaces
    /// to and from JSON tuples.
    /// </summary>
    public class NamespaceTupleConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Namespace);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);

            if (token.Type == JTokenType.Array)
            {
                return new Namespace((JArray)token);
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
