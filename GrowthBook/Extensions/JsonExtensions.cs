using System.Text.Json.Nodes;
using System.Text.Json;
using GrowthBook.Utilities;

namespace GrowthBook.Extensions
{
    internal static class JsonExtensions
    {
        /// <summary>
        /// Determines whether the <see cref="JsonNode"/> is either null or a JSON null value.
        /// </summary>
        /// <param name="node">The JSON node to verify.</param>
        /// <returns>True if null, false otherwise.</returns>
        public static bool IsNull(this JsonNode? node) => node is null || node is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null;

        /// <summary>
        /// Determines whether the <see cref="JsonNode"/> is null, a JSON null value, an empty string, or whitespace.
        /// </summary>
        /// <param name="node">The JSON node to verify.</param>
        /// <returns>True if null, empty, or whitespace, false otherwise.</returns>
        public static bool IsNullOrWhitespace(this JsonNode? node)
        {
            if (node.IsNull())
            {
                return true;
            }

            if (node is JsonValue jv && jv.TryGetValue(out string? strValue))
            {
                return strValue.IsNullOrWhitespace();
            }

            return false;
        }

        /// <summary>
        /// Gets the value of the named attribute key within the current <see cref="JsonObject"/>.
        /// </summary>
        /// <param name="json">The JSON object to look up the key from.</param>
        /// <param name="attributeKey">The key of the attribute value in the JSON object. Defaults to "id" when not provided.</param>
        /// <returns>The value associated with the requested attribute, or null if the value is null or a JSON null value.</returns>
        public static (string? Attribute, string? Value) GetHashAttributeAndValue(this JsonObject? json, string? attributeKey = null, string? fallbackAttributeKey = null)
        {
            var attribute = attributeKey ?? "id";
            var attributeNode = json?[attribute];

            var isNullOrMissing = attributeNode.IsNull();

            if (isNullOrMissing && !string.IsNullOrWhiteSpace(fallbackAttributeKey))
            {
                return (fallbackAttributeKey, json?[fallbackAttributeKey]?.ToString());
            }

            return (attribute, attributeNode?.ToString());
        }
    }
}
