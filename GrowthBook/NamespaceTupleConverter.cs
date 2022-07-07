using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace GrowthBook {
    /// <summary>
    /// Represents a JsonConverter object used to convert Namespaces
    /// to and from JSON tuples.
    /// </summary>
    public class NamespaceTupleConverter : JsonConverter {
        public override bool CanConvert(Type objectType) {
            return objectType == typeof(Namespace);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) {
            JToken token = JToken.Load(reader);
            if (token.Type == JTokenType.Array) {
                return new Namespace((JArray)token);
            }
            return new JsonSerializer().Deserialize(reader, objectType);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) {
            Namespace valueNamespace = (Namespace)value;
            JArray t = new JArray();
            t.Add(JToken.FromObject(valueNamespace.Id));
            t.Add(JToken.FromObject(valueNamespace.Start));
            t.Add(JToken.FromObject(valueNamespace.End));
            t.WriteTo(writer);
        }
    }
}
