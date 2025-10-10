using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GrowthBook.Converters
{
    /// <summary>
    /// Represents a JsonConverter object used to convert BucketRanges
    /// to and from JSON tuples using System.Text.Json.
    /// </summary>
    public class BucketRangeConverter : JsonConverter<BucketRange>
    {
        // Не потрібен CanConvert

        public override BucketRange? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader);

            if (node is JsonArray array && array.Count >= 2)
            {
                // Використовуємо GetValue<double>() для безпечного отримання значень
                var start = array[0]!.GetValue<double>();
                var end = array[1]!.GetValue<double>();

                return new BucketRange(start, end);
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, BucketRange value, JsonSerializerOptions options)
        {
            // Серіалізуємо як масив: [start, end]
            writer.WriteStartArray();

            // Записуємо елементи напряму
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);

            writer.WriteEndArray();
        }
    }
}
