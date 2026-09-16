using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GrowthBook.Services
{
    /// <summary>
    /// A sticky bucket store backed by Redis, so a user keeps the same variation across every instance in a
    /// fleet and across restarts. With the in-memory store each process buckets the same user independently,
    /// which makes sticky bucketing unreliable in any multi-instance deployment.
    /// </summary>
    public class RedisStickyBucketService : IAsyncStickyBucketService
    {
        /// <summary>
        /// The reference SDKs store documents with camelCase field names. Serializing with the CLR property
        /// names instead would let this SDK read their documents but not the other way round, which defeats
        /// the point of a store shared across a polyglot fleet. Scoped to this service rather than applied
        /// to the document type, so nothing else that serializes it changes shape.
        /// </summary>
        private static readonly JsonSerializerSettings WireFormat = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private readonly IRedisCompatibleClient _redis;
        private readonly string _keyPrefix;
        private readonly ILogger<RedisStickyBucketService> _logger;

        /// <summary>
        /// Creates a Redis-backed sticky bucket store.
        /// </summary>
        /// <param name="redis">An adapter over the application's Redis client.</param>
        /// <param name="keyPrefix">
        /// Prepended to every key, for sharing one Redis instance between applications or environments.
        /// </param>
        /// <param name="logger">Optional logger.</param>
        public RedisStickyBucketService(IRedisCompatibleClient redis, string keyPrefix = "", ILogger<RedisStickyBucketService> logger = null)
        {
            _redis = redis ?? throw new ArgumentNullException(nameof(redis));
            _keyPrefix = keyPrefix ?? string.Empty;
            _logger = logger ?? NullLogger<RedisStickyBucketService>.Instance;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A single round trip for every attribute, rather than one per attribute. The reference SDK's
        /// Redis service overrides the batch method the same way and leaves the single-document read
        /// unimplemented; here it delegates to this one, since C# has no default interface members to
        /// inherit a loop from.
        /// </remarks>
        public async Task<IDictionary<string, StickyAssignmentsDocument>> GetAllAssignmentsAsync(IEnumerable<string> attributes, CancellationToken cancellationToken = default)
        {
            var documents = new Dictionary<string, StickyAssignmentsDocument>();

            var formattedAttributes = attributes?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray()
                ?? new string[0];

            if (formattedAttributes.Length == 0)
            {
                return documents;
            }

            var keys = formattedAttributes.Select(ToRedisKey).ToArray();

            string[] values;

            try
            {
                values = await _redis.MultiGetAsync(keys, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read '{KeyCount}' sticky bucket document(s) from Redis, continuing without them", keys.Length);

                return documents;
            }

            if (values is null)
            {
                return documents;
            }

            foreach (var value in values)
            {
                var document = Deserialize(value);

                if (document != null)
                {
                    documents[document.FormattedAttribute] = document;
                }
            }

            return documents;
        }

        /// <inheritdoc/>
        public async Task<StickyAssignmentsDocument> GetAssignmentsAsync(string attributeName, string attributeValue, CancellationToken cancellationToken = default)
        {
            var formattedAttribute = $"{attributeName}||{attributeValue}";

            var documents = await GetAllAssignmentsAsync(new[] { formattedAttribute }, cancellationToken).ConfigureAwait(false);

            return documents.TryGetValue(formattedAttribute, out var document) ? document : null;
        }

        /// <inheritdoc/>
        public async Task SaveAssignmentsAsync(StickyAssignmentsDocument document, CancellationToken cancellationToken = default)
        {
            if (document is null)
            {
                return;
            }

            try
            {
                await _redis.SetAsync(ToRedisKey(document.FormattedAttribute), JsonConvert.SerializeObject(document, WireFormat), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save the sticky bucket document for '{Attribute}' to Redis", document.FormattedAttribute);
            }
        }

        private string ToRedisKey(string formattedAttribute) => _keyPrefix + formattedAttribute;

        /// <summary>
        /// Turns a stored value back into a document, rejecting anything that isn't a usable one. A single
        /// unreadable entry - hand-edited, written by a different version, truncated - must not take the
        /// whole batch down with it, so failures are dropped rather than thrown.
        /// </summary>
        private StickyAssignmentsDocument Deserialize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            StickyAssignmentsDocument document;

            try
            {
                // The same settings the write side uses. Newtonsoft happens to match property names
                // case-insensitively on the way in, so leaving them off worked by accident - but only until
                // a property is renamed or given a [JsonProperty], at which point reads would start silently
                // returning documents with empty fields.
                document = JsonConvert.DeserializeObject<StickyAssignmentsDocument>(value, WireFormat);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ignoring a sticky bucket document from Redis that could not be parsed");

                return null;
            }

            if (document?.AttributeName is null || document.AttributeValue is null || document.Assignments is null)
            {
                _logger.LogWarning("Ignoring a sticky bucket document from Redis that was missing required fields");

                return null;
            }

            return document;
        }
    }
}
