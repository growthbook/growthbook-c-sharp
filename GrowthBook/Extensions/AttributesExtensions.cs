using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Extensions
{
    /// <summary>
    /// Extension methods for easier work with user attributes in GrowthBook.
    /// </summary>
    public static class AttributesExtensions
    {
        /// <summary>
        /// Creates a GrowthBook instance with user attributes using fluent API.
        /// </summary>
        /// <param name="context">Base context</param>
        /// <param name="attributes">User attributes as IDictionary</param>
        /// <returns>New GrowthBook instance</returns>
        public static GrowthBook WithAttributes(this Context context, IDictionary<string, object> attributes)
        {
            var newContext = context.Clone();
            newContext.SetAttributes(attributes);
            return new GrowthBook(newContext);
        }

        /// <summary>
        /// Creates a GrowthBook instance with user attributes using fluent API.
        /// </summary>
        /// <param name="context">Base context</param>
        /// <param name="attributes">User attributes as anonymous object</param>
        /// <returns>New GrowthBook instance</returns>
        public static GrowthBook WithAttributes(this Context context, object attributes)
        {
            var newContext = context.Clone();
            newContext.SetAttributes(attributes);
            return new GrowthBook(newContext);
        }

        /// <summary>
        /// Updates GrowthBook attributes using fluent API.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="attributes">New user attributes as IDictionary</param>
        /// <returns>Same GrowthBook instance for chaining</returns>
        public static GrowthBook WithUpdatedAttributes(this GrowthBook growthBook, IDictionary<string, object> attributes)
        {
            growthBook.UpdateAttributes(attributes);
            return growthBook;
        }

        /// <summary>
        /// Updates GrowthBook attributes using fluent API.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="attributes">New user attributes as anonymous object</param>
        /// <returns>Same GrowthBook instance for chaining</returns>
        public static GrowthBook WithUpdatedAttributes(this GrowthBook growthBook, object attributes)
        {
            growthBook.UpdateAttributes(attributes);
            return growthBook;
        }

        /// <summary>
        /// Adds or updates a single attribute using fluent API.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="key">Attribute key</param>
        /// <param name="value">Attribute value</param>
        /// <returns>Same GrowthBook instance for chaining</returns>
        public static GrowthBook WithAttribute(this GrowthBook growthBook, string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("Attribute key cannot be null or empty", nameof(key));

            growthBook.Attributes[key] = JToken.FromObject(value);
            return growthBook;
        }

        /// <summary>
        /// Removes an attribute using fluent API.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="key">Attribute key to remove</param>
        /// <returns>Same GrowthBook instance for chaining</returns>
        public static GrowthBook WithoutAttribute(this GrowthBook growthBook, string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                growthBook.Attributes.Remove(key);
            }
            return growthBook;
        }

        /// <summary>
        /// Gets an attribute value with type conversion.
        /// </summary>
        /// <typeparam name="T">Expected attribute type</typeparam>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="key">Attribute key</param>
        /// <param name="defaultValue">Default value if attribute doesn't exist</param>
        /// <returns>Attribute value or default</returns>
        public static T GetAttribute<T>(this GrowthBook growthBook, string key, T defaultValue = default(T))
        {
            if (string.IsNullOrEmpty(key) || growthBook.Attributes == null)
                return defaultValue;

            if (growthBook.Attributes.TryGetValue(key, out JToken token))
            {
                try
                {
                    return token.ToObject<T>();
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Checks if an attribute exists.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <param name="key">Attribute key</param>
        /// <returns>True if attribute exists</returns>
        public static bool HasAttribute(this GrowthBook growthBook, string key)
        {
            return !string.IsNullOrEmpty(key) && 
                   growthBook.Attributes != null && 
                   growthBook.Attributes.ContainsKey(key);
        }

        /// <summary>
        /// Gets all attribute keys.
        /// </summary>
        /// <param name="growthBook">GrowthBook instance</param>
        /// <returns>Collection of attribute keys</returns>
        public static IEnumerable<string> GetAttributeKeys(this GrowthBook growthBook)
        {
            if (growthBook.Attributes == null)
                return new string[0];

            return growthBook.Attributes.Properties().Select(p => p.Name);
        }
    }
}