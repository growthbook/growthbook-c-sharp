using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GrowthBook.Converters
{
    /// <summary>
    /// Custom JSON converter for <see cref="BucketRange"/>.
    /// Serializes the object as a JSON array [start, end] and deserializes it back.
    /// </summary>
    public class BucketRangeConverter : JsonConverter<BucketRange>
    {

        /// <summary>
        /// Reads a JSON array and converts it into a <see cref="BucketRange"/> instance.
        /// Expected format: [start, end]
        /// </summary>
        /// <param name="reader">JSON reader instance.</param>
        /// <param name="typeToConvert">Target type to convert to.</param>
        /// <param name="options">Serialization options.</param>
        /// <returns>A <see cref="BucketRange"/> object or null if the JSON is invalid.</returns>
        public override BucketRange? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var node = JsonNode.Parse(ref reader);

            if (node is JsonArray array && array.Count >= 2)
            {
                var start = array[0]!.GetValue<double>();
                var end = array[1]!.GetValue<double>();

                return new BucketRange(start, end);
            }

            return null;
        }

        /// <summary>
        /// Writes a <see cref="BucketRange"/> instance as a JSON array [start, end].
        /// </summary>
        /// <param name="writer">JSON writer instance.</param>
        /// <param name="value">Bucket range to serialize.</param>
        /// <param name="options">Serialization options.</param>
        public override void Write(Utf8JsonWriter writer, BucketRange value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();

            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.End);

            writer.WriteEndArray();
        }
    }
}
