using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Extensions
{
    public static class JsonExtensions
    {
        public static bool IsNull(this JObject json) => json is null || json.Type == JTokenType.Null;
        public static bool IsNull(this JToken token) => token is null || token.Type == JTokenType.Null;
        public static string TryToHashWith(this JObject json, string attributeKey = null)
        {
            var attribute = attributeKey ?? "id";

            if (json.ContainsKey(attribute))
            {
                return json[attribute].ToString();
            }

            return null;
        }
    }
}
