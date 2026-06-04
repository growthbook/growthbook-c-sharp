using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GrowthBook.Converters
{
    /// <summary>
    /// Custom JSON converter for <see cref="Namespace"/>.
    /// Serializes the object as a JSON array [id, start, end] and deserializes it back.
    /// </summary>
    public class NamespaceConverter : JsonConverter<Namespace>
    {
        /// <summary>
        /// Reads a JSON array and converts it into a <see cref="Namespace"/> instance.
        /// Expected format: [id, start, end]
        /// </summary>
        /// <param name="reader">JSON reader instance.</param>
        /// <param name="typeToConvert">Target type to convert to.</param>
        /// <param name="options">Serialization options.</param>
        /// <returns>A <see cref="Namespace"/> object or null if invalid JSON.</returns>
        public override Namespace? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader);

            if (node is JsonArray array && array.Count >= 3)
            {
                return new Namespace(array);
            }

            return null;
        }

        /// <summary>
        /// Writes a <see cref="Namespace"/> instance as a JSON array [id, start, end].
        /// </summary>
        /// <param name="writer">JSON writer instance.</param>
        /// <param name="value">Namespace to serialize.</param>
        /// <param name="options">Serialization options.</param>
        public override void Write(Utf8JsonWriter writer, Namespace value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            writer.WriteStringValue(value.Id);
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);

            writer.WriteEndArray();
        }
    }
}
