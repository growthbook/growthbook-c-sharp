using System;
using System.Collections.Generic;
using System.Text;
using GrowthBook.Utilities;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Extensions
{
    internal static class JsonExtensions
    {
        /// <summary>
        /// Determines whether the <see cref="JObject"/> is either null or of <see cref="JTokenType.Null"/>.
        /// </summary>
        /// <param name="json">The JSON object to verify.</param>
        /// <returns>True if null, false otherwise.</returns>
        public static bool IsNull(this JObject? json) => json is null || json.Type == JTokenType.Null;

        /// <summary>
        /// Determines whether the <see cref="JToken"/> is either null or of <see cref="JTokenType.Null"/>.
        /// </summary>
        /// <param name="token">The JSON token to verify.</param>
        /// <returns>True if null, false otherwise.</returns>
        public static bool IsNull(this JToken? token) => token is null || token.Type == JTokenType.Null;

        /// <summary>
        /// Determines whether the <see cref="JToken"/> is either null, <see cref="JTokenType.Null"/>, an empty string, or whitespace.
        /// </summary>
        /// <param name="token">The JSON token to verify.</param>
        /// <returns>True if null, empty, or whitespace, false otherwise.</returns>
        public static bool IsNullOrWhitespace(this JToken? token) => token == null || token.IsNull() || token.ToString().IsNullOrWhitespace();

        /// <summary>
        /// Gets the value of the named attribute key within the current <see cref="JObject"/>.
        /// </summary>
        /// <param name="json">The JSON object to look up the key from.</param>
        /// <param name="attributeKey">The key of the attribute value in the JSON object. Defaults to "id" when not provided.</param>
        /// <returns>The value associated with the requested attribute, or null if the value is null or <see cref="JTokenType.Null"/>.</returns>
        public static (string? Attribute, string? Value) GetHashAttributeAndValue(this JObject? json, string? attributeKey = null, string? fallbackAttributeKey = null)
        {
            var attribute = attributeKey ?? "id";

            var attributeValue = json?[attribute];

            if ((attributeValue == null || attributeValue.Type == JTokenType.Null) && fallbackAttributeKey != null)
            {
                return (fallbackAttributeKey, json?[fallbackAttributeKey]?.ToString());
            }

            return (attribute, attributeValue?.ToString());
        }

        public static JArray AsArray(this JToken token) => (JArray)token;

        public static JArray AsArray(this JProperty property) => (JArray)property.Value;

        public static JObject AsObject(this JProperty property) => (JObject)property.Value;
    }
}
