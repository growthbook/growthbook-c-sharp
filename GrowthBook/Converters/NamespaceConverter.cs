using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GrowthBook.Converters
{
    /// <summary>
    /// Represents a JsonConverter object used to convert Namespaces
    /// to and from JSON tuples using System.Text.Json.
    /// </summary>
    // Тепер успадковується від STJ-конвертера
    public class NamespaceConverter : JsonConverter<Namespace>
    {
        // Методи CanConvert більше не потрібні

        // Реалізація десеріалізації
        public override Namespace? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader);

            if (node is JsonArray array && array.Count >= 3)
            {
                // Викликаємо оновлений конструктор, що приймає JsonArray
                return new Namespace(array);
            }

            return null;
        }

        // Реалізація серіалізації
        public override void Write(Utf8JsonWriter writer, Namespace value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            // Записуємо елементи напряму
            writer.WriteStringValue(value.Id);
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);

            writer.WriteEndArray();
        }
    }
}
