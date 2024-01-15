using System;
using System.Collections.Generic;
using System.Text;
using GrowthBook.Utilities;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Extensions
{
    internal static class JsonExtensions
    {
        public static bool IsNull(this JObject json) => json is null || json.Type == JTokenType.Null;
        public static bool IsNull(this JToken token) => token is null || token.Type == JTokenType.Null;

        public static string GetHashAttributeValue(this JObject json, string attributeKey = null)
        {
            var attribute = attributeKey ?? "id";

            var attributeValue = json[attribute];

            if (attributeValue.IsNull())
            {
                return null;
            }

            return attributeValue.ToString();
        }
    }
}
