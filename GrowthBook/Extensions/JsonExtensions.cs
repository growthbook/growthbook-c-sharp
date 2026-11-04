using System;
using System.Collections.Generic;
using System.Text;
using GrowthBook.Providers;
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
        public static bool IsNull(this JObject json) => json is null || json.Type == JTokenType.Null;

        /// <summary>
        /// Determines whether the <see cref="JToken"/> is either null or of <see cref="JTokenType.Null"/>.
        /// </summary>
        /// <param name="token">The JSON token to verify.</param>
        /// <returns>True if null, false otherwise.</returns>
        public static bool IsNull(this JToken token) => token is null || token.Type == JTokenType.Null;

        /// <summary>
        /// Determines whether the <see cref="JToken"/> is either null, <see cref="JTokenType.Null"/>, an empty string, or whitespace.
        /// </summary>
        /// <param name="token">The JSON token to verify.</param>
        /// <returns>True if null, empty, or whitespace, false otherwise.</returns>
        public static bool IsNullOrWhitespace(this JToken token) => token.IsNull() || token.ToString().IsNullOrWhitespace();

        /// <summary>
        /// Gets the attribute and value that identify a user for hashing, the way the JavaScript SDK does.
        /// Only a non-empty string or a finite, non-zero number counts; any other value, including <c>0</c>,
        /// <c>""</c>, booleans, arrays and objects, counts as missing. When the attribute is missing the
        /// fallback attribute is tried with the same rule.
        /// </summary>
        /// <param name="json">The attributes to look up the key from.</param>
        /// <param name="attributeKey">The hash attribute. Defaults to "id" when null or empty.</param>
        /// <param name="fallbackAttributeKey">The attribute to try when the hash attribute is missing, if any.</param>
        /// <returns>
        /// The attribute used and its value as text (numbers written like JavaScript's <c>String(number)</c>),
        /// or the hash attribute and null when neither attribute has a usable value.
        /// </returns>
        public static (string Attribute, string Value) GetHashAttributeAndValue(this JObject json, string attributeKey = null, string fallbackAttributeKey = null)
        {
            var attribute = string.IsNullOrEmpty(attributeKey) ? "id" : attributeKey;

            var value = HashableText(json?[attribute]);

            if (value is null && !string.IsNullOrEmpty(fallbackAttributeKey))
            {
                var fallbackValue = HashableText(json?[fallbackAttributeKey]);

                if (fallbackValue != null)
                {
                    return (fallbackAttributeKey, fallbackValue);
                }
            }

            return (attribute, value);
        }

        /// <summary>
        /// Gets the text to hash for an attribute value, or null if the value can't identify a user.
        /// </summary>
        private static string HashableText(JToken token)
        {
            switch (token?.Type)
            {
                case JTokenType.String:
                    var text = token.ToString();
                    return text.Length == 0 ? null : text;
                case JTokenType.Date:
                    // Json.NET read an ISO 8601 string as a date, but it is still a string in the payload
                    return ConditionEvaluationProvider.StringValue(token);
                case JTokenType.Integer:
                case JTokenType.Float:
                    var number = ConditionEvaluationProvider.DoubleValue(token);
                    return number == 0 || double.IsNaN(number) || double.IsInfinity(number)
                        ? null
                        : ConditionEvaluationProvider.JavaScriptNumberText(number);
                default:
                    return null;
            }
        }

        public static JArray AsArray(this JToken token) => (JArray)token;

        public static JArray AsArray(this JProperty property) => (JArray)property.Value;

        public static JObject AsObject(this JProperty property) => (JObject)property.Value;
    }
}
