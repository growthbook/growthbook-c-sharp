using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Services
{
    /// <summary>
    /// Keeps sticky bucket assignments in Redis, as <c>RedisStickyBucketService</c> in the JavaScript SDK: one key per
    /// user identifier, <c>{prefix}{attributeName}||{attributeValue}</c>, holding the document as
    /// <c>{"attributeName", "attributeValue", "assignments"}</c>. The format is the same, so services written in either
    /// SDK can share one Redis.
    /// </summary>
    /// <remarks>
    /// A document that cannot be parsed, or that names another identifier than the key it was read from, is skipped.
    /// A client that fails throws, so a caller can tell an unreachable store from one that holds nothing.
    /// </remarks>
    public class RedisStickyBucketService : IStickyBucketService
    {
        private readonly IRedisCompatibleClient _client;
        private readonly string _prefix;

        /// <param name="client">The Redis client to read and write through.</param>
        /// <param name="prefix">Prepended to every key, to keep these apart from the application's own.</param>
        public RedisStickyBucketService(IRedisCompatibleClient client, string prefix = "")
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _prefix = prefix ?? string.Empty;
        }

        public StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue)
        {
            var identifier = new StickyAssignmentsDocument(attributeName, attributeValue).FormattedAttribute;

            return GetAllAssignments(new[] { identifier }).TryGetValue(identifier, out var document) ? document : null;
        }

        public IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes)
        {
            var identifiers = attributes?.Distinct().ToArray() ?? Array.Empty<string>();
            var documents = new Dictionary<string, StickyAssignmentsDocument>();

            if (identifiers.Length == 0)
            {
                return documents;
            }

            var values = _client.MultiGet(identifiers.Select(identifier => _prefix + identifier).ToArray()) ?? Array.Empty<string>();

            for (var i = 0; i < identifiers.Length && i < values.Length; i++)
            {
                var document = Parse(values[i]);

                if (document != null && document.FormattedAttribute == identifiers[i])
                {
                    documents[identifiers[i]] = document;
                }
            }

            return documents;
        }

        public void SaveAssignments(StickyAssignmentsDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var json = new JObject
            {
                ["attributeName"] = document.AttributeName,
                ["attributeValue"] = document.AttributeValue,
                ["assignments"] = JObject.FromObject(document.Assignments ?? new Dictionary<string, string>())
            };

            _client.Set(_prefix + document.FormattedAttribute, json.ToString(Formatting.None));
        }

        private static StickyAssignmentsDocument Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            try
            {
                // Read as written: a value that looks like a date stays the text it was.
                using (var reader = new JsonTextReader(new StringReader(value)) { DateParseHandling = DateParseHandling.None })
                {
                    if (!(JToken.ReadFrom(reader) is JObject json)
                        || !(json["attributeName"] is JValue name) || name.Type != JTokenType.String
                        || !(json["attributeValue"] is JValue attributeValue) || attributeValue.Type == JTokenType.Null
                        || !(json["assignments"] is JObject assignments))
                    {
                        return null;
                    }

                    var parsed = assignments.Properties()
                        .Where(property => property.Value.Type != JTokenType.Null)
                        .ToDictionary(property => property.Name, property => property.Value.ToString());

                    return new StickyAssignmentsDocument(name.ToString(), attributeValue.ToString(), parsed);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
