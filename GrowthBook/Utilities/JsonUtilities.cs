using Newtonsoft.Json;

namespace GrowthBook.Utilities
{
    internal static class JsonUtilities
    {
        /// <summary>
        /// Settings for reading a features payload. Json.NET reads ISO 8601 strings as dates by default, which
        /// loses their original text; conditions compare strings as text, so timestamps are kept as strings.
        /// </summary>
        public static readonly JsonSerializerSettings PayloadSettings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None
        };
    }
}
