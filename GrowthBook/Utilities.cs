using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Utility functions used by the GrowthBook class.
    /// </summary>
    public static class Utilities
    {
        /// <summary>
        /// Hashes a string to a double between 0 and 1 using the simple Fowler–Noll–Vo algorithm (fnv32a).
        /// </summary>
        /// <param name="value">The string to hash.</param>
        /// <returns>Double between 0 and 1, null if an unsupported version.</returns>
        public static double? Hash(string seed, string value, int version)
        {
            if (version == 2) // New hashing algorithm
            {
                var n = FNV32A(FNV32A(seed + value) + "");
                return (n % 10000) / 10000;
            }
            else if (version == 1) // Original hashing algorithm (with a bias flaw)
            {
                var n = FNV32A(value + seed);
                return (n % 1000) / 1000;
            }

            return null;
        }

        public static bool InRange(double number, BucketRange range) => number >= range.Start && number < range.End;

        /// <summary>
        /// Checks if a userId is within an experiment namespace or not.
        /// </summary>
        /// <param name="userId">The user id string to check.</param>
        /// <param name="nSpace">The namespace to check.</param>
        /// <returns>True if the userid is within the experiment namespace.</returns>
        public static bool InNamespace(string userId, Namespace nSpace)
        {
            var n = Hash("__" + nSpace.Id, userId, 1);
            return n >= nSpace.Start && n < nSpace.End;
        }

        /// <summary>
        /// Returns an array of doubles with numVariations items that are all equal and sum to 1.
        /// </summary>
        /// <param name="numVariations">The number of variations to generate weights for.</param>
        /// <returns>Array of doubles with numVariations items that are all equal and sum to 1.</returns>
        public static IEnumerable<double> GetEqualWeights(int numVariations)
        {
            if (numVariations >= 1)
            {
                for (int i = 0; i < numVariations; i++)
                {
                    yield return (1.0 / numVariations);
                }
            }
        }

        /// <summary>
        /// Converts an experiment's coverage and variation weights into an list of bucket ranges.
        /// </summary>
        /// <param name="numVariations">The number of variations to convert.</param>
        /// <param name="coverage">The experiment's coverage (defaults to 1).</param>
        /// <param name="weights">Optional list of variant weights.</param>
        /// <returns>A list of bucket ranges.</returns>
        public static IEnumerable<BucketRange> GetBucketRanges(int numVariations, double coverage = 1, IEnumerable<double> weights = null)
        {
            if (coverage < 0)
            {
                coverage = 0;
            }
            else if (coverage > 1)
            {
                coverage = 1;
            }

            var allWeights = weights?.ToArray();

            if (allWeights == null || allWeights.Length != numVariations)
            {
                weights = GetEqualWeights(numVariations);
            }

            double totalWeight = weights.Sum();
            if (totalWeight < 0.99 || totalWeight > 1.01d)
            {
                weights = GetEqualWeights(numVariations);
            }

            var cumulative = 0d;

            for (int i = 0; i < allWeights.Length; i++)
            {
                var start = cumulative;
                cumulative += allWeights[i];
                yield return new BucketRange(start, start + coverage * allWeights[i]);
            }
        }

        /// <summary>
        /// Given a hash and bucket ranges, assign one of the bucket ranges.
        /// </summary>
        /// <param name="n">The hash value.</param>
        /// <param name="ranges">LIst of bucket ranges to compare the hash to.</param>
        /// <returns>The selected variation id, or -1 if no match is found.</returns>
        public static int ChooseVariation(double n, IList<BucketRange> ranges)
        {
            for (int i = 0; i < ranges.Count; i++)
            {
                if (InRange(n, ranges[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Checks if an experiment variation is being forced via a URL query string.
        /// </summary>
        /// <param name="id">The id field to search for in the query string.</param>
        /// <param name="url">The url to search.</param>
        /// <param name="numVariations">The number of variations in the experiment.</param>
        /// <returns>The overridden variation id, or null if not found.</returns>
        public static int? GetQueryStringOverride(string id, string url, int numVariations)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var res = new Uri(url);
            if (string.IsNullOrWhiteSpace(res.Query))
            {
                return null;
            }

            NameValueCollection qs = HttpUtility.ParseQueryString(res.Query);
            var variation = qs.Get(id);

            if (string.IsNullOrWhiteSpace(variation))
            {
                return null;
            }

            if (!int.TryParse(variation, out var varId))
            {
                return null;
            }

            if (varId < 0 || varId >= numVariations)
            {
                return null;
            }

            return varId;
        }

        public static string Decrypt(string encryptedString, string decryptionKey)
        {
            var parts = encryptedString.Split('.');

            var iv = Convert.FromBase64String(parts[0]);

            // TODO: Implement AES-CBC 128k here

            return null;
        }

        /// <summary>
        /// The main function used to evaluate a condition.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy the condition.</returns>
        public static bool EvalCondition(JToken attributes, JObject condition)
        {
            if (condition.ContainsKey("$or"))
            {
                return EvalOr(attributes, (JArray)condition["$or"]);
            }
            if (condition.ContainsKey("$nor"))
            {
                return !EvalOr(attributes, (JArray)condition["$nor"]);
            }
            if (condition.ContainsKey("$and"))
            {
                return EvalAnd(attributes, (JArray)condition["$and"]);
            }
            if (condition.ContainsKey("$not"))
            {
                return !EvalCondition(attributes, (JObject)condition["$not"]);
            }

            foreach (JProperty property in condition.Properties())
            {
                if (!EvalConditionValue(property.Value, GetPath(attributes, property.Name)))
                {
                    return false;
                }
            }

            return true;
        }

        // #region Private Helpers

        /// <summary>
        /// Implementation of the Fowler–Noll–Vo algorithm (fnv32a) algorithm.
        /// </summary>
        /// <param name="value">The value to hash.</param>
        /// <returns>The hashed value.</returns>
        static uint FNV32A(string value)
        {
            // TODO: Make sure FNV32A implementation is correct.
            uint hash = 0x811c9dc5;
            uint prime = 0x01000193;

            foreach (char c in value.ToCharArray())
            {
                hash ^= c;
                hash *= prime;
            }

            return hash;
        }

        /// <summary>
        /// Returns true if the attributes satisfy any of the conditions.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy any of the conditions.</returns>
        static bool EvalOr(JToken attributes, JArray conditions)
        {
            if (conditions.Count == 0)
            {
                return true;
            }

            foreach (JObject condition in conditions)
            {
                if (EvalCondition(attributes, condition))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if the attributes satisfy all of the conditions.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy all of the conditions.</returns>
        static bool EvalAnd(JToken attributes, JArray conditions)
        {
            foreach (JObject condition in conditions)
            {
                if (!EvalCondition(attributes, condition))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks to see if every key in the object is an operator.
        /// </summary>
        /// <param name="obj">The object to check.</param>
        /// <returns>True if every key in the object starts with $.</returns>
        static bool IsOperatorObject(JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (!property.Name.StartsWith("$"))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks to see if a condition value matches an attribute value.
        /// </summary>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if the condition value matches the attribute value.</returns>
        static bool EvalConditionValue(JToken conditionValue, JToken attributeValue)
        {
            if (conditionValue.Type == JTokenType.Object)
            {
                JObject conditionObj = (JObject)conditionValue;

                if (IsOperatorObject(conditionObj))
                {
                    foreach (JProperty property in conditionObj.Properties())
                    {
                        if (!EvalOperatorCondition(property.Name, attributeValue, property.Value))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            return JToken.DeepEquals(conditionValue ?? JValue.CreateNull(), attributeValue ?? JValue.CreateNull());
        }

        /// <summary>
        /// Checks if attributeValue is an array, and if so at least one of the array items must match the condition.
        /// </summary>
        /// <param name="condition">The condition to check.</param>
        /// <param name="attributeVaue">The attribute value to check.</param>
        /// <returns>True if attributeValue is an array and at least one of the array items matches the condition.</returns>
        static bool ElemMatch(JObject condition, JToken attributeVaue)
        {
            if (attributeVaue.Type != JTokenType.Array)
            {
                return false;
            }

            foreach (JToken elem in (JArray)attributeVaue)
            {
                if (IsOperatorObject(condition) && EvalConditionValue(condition, elem))
                {
                    return true;
                }
                if (EvalCondition(elem, condition))
                {
                    return true;
                }
            }

            return false;
        }

        static string PaddedVersionString(string input)
        {
            // Remove build info and leading `v` if any
            // Split version into parts (both core version numbers and pre-release tags)
            // "v1.2.3-rc.1+build123" -> ["1","2","3","rc","1"]

            var trimmedVersion = Regex.Replace(input, @"(^v|\+.*$)", string.Empty);
            var versionParts = Regex.Split(trimmedVersion, "[-.]").ToList();

            // If it's SemVer without a pre-release, add `~` to the end
            // ["1","0","0"] -> ["1","0","0","~"]
            // "~" is the largest ASCII character, so this will make "1.0.0" greater than "1.0.0-beta" for example

            if (versionParts.Count == 3)
            {
                versionParts.Add("~");
            }

            // Left pad each numeric part with spaces so string comparisons will work ("9">"10", but " 9"<"10")
            // Then, join back together into a single string

            var paddedVersionParts = versionParts.Select(x => Regex.IsMatch(x, "^[0-9]+$") ? x.PadLeft(5, ' ') : x);

            return string.Join("-", paddedVersionParts);
        }

        static bool IsIn(JToken conditionValue, JToken actualValue)
        {
            if (actualValue.Type == JTokenType.Array)
            {
                var element = ((JArray)actualValue).FirstOrDefault(x => x.ToString() == conditionValue.ToString());

                return element != null;
            }
            else
            {
                return conditionValue.ToString().Contains(actualValue.ToString());
            }
        }

        /// <summary>
        /// A switch that handles all the possible operators.
        /// </summary>
        /// <param name="op">The operator to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <returns></returns>
        static bool EvalOperatorCondition(string op, JToken attributeValue, JToken conditionValue)
        {
            if (op == "$eq")
            {
                return conditionValue.Equals(attributeValue);
            }
            if (op == "$ne")
            {
                return !conditionValue.Equals(attributeValue);
            }
            if (attributeValue is IComparable attrComp)
            {
                if (op == "$lt")
                {
                    return attrComp.CompareTo(conditionValue) < 0;
                }
                if (op == "$lte")
                {
                    return attrComp.CompareTo(conditionValue) <= 0;
                }
                if (op == "$gt")
                {
                    return attrComp.CompareTo(conditionValue) > 0;
                }
                if (op == "$gte")
                {
                    return attrComp.CompareTo(conditionValue) >= 0;
                }
            }
            if (op == "$regex")
            {
                try
                {
                    return Regex.IsMatch(attributeValue?.ToString(), conditionValue?.ToString());
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            if (op == "$in")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return IsIn(conditionValue, attributeValue);
            }
            if (op == "$nin")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return !IsIn(conditionValue, attributeValue);
            }
            if (op == "$all")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                if (attributeValue?.Type != JTokenType.Array)
                {
                    return false;
                }

                var conditionList = (JArray)conditionValue;
                var attributeList = (JArray)attributeValue;

                foreach (JToken condition in conditionList)
                {
                    if (!attributeList.Any(x => EvalConditionValue(condition, x)))
                    {
                        return false;
                    }

                    return true;
                }

                return true;                
            }
            
            if (op == "$elemMatch")
            {
                return ElemMatch((JObject)conditionValue, attributeValue);
            }
            if (op == "$size")
            {
                if (attributeValue?.Type != JTokenType.Array)
                {
                    return false;
                }

                return EvalConditionValue(conditionValue, ((JArray)attributeValue).Count);                
            }
            if (op == "$exists")
            {
                var value = conditionValue.ToObject<bool>();

                if (!value)
                {
                    return attributeValue == null || attributeValue.Type == JTokenType.Null;
                }

                return attributeValue != null && attributeValue.Type != JTokenType.Null;
            }
            if (op == "$type")
            {
                return GetType(attributeValue) == conditionValue.ToString();
            }
            if (op == "$not")
            {
                return !EvalConditionValue(conditionValue, attributeValue);
            }
            if (op == "$veq")
            {
                return PaddedVersionString(conditionValue.ToString()) == PaddedVersionString(attributeValue.ToString());
            }
            if (op == "$vne")
            {
                return PaddedVersionString(conditionValue.ToString()) != PaddedVersionString(attributeValue.ToString());
            }
            if (op == "$vlt")
            {
                return PaddedVersionString(conditionValue.ToString()).CompareTo(PaddedVersionString(attributeValue.ToString())) > 0;
            }
            if (op == "$vlte")
            {
                var value = PaddedVersionString(conditionValue.ToString()).CompareTo(PaddedVersionString(attributeValue.ToString()));
                return value >= 0;
            }
            if (op == "$vgt")
            {
                return PaddedVersionString(conditionValue.ToString()).CompareTo(PaddedVersionString(attributeValue.ToString())) < 0;
            }
            if (op == "$vgte")
            {
                var value = PaddedVersionString(conditionValue.ToString()).CompareTo(PaddedVersionString(attributeValue.ToString()));
                return value <= 0;
            }

            // TODO: Log the error for debug purposes?

            return false;
        }

        static JToken GetPath(JToken attributes, string key) => attributes.SelectToken(key);

        /// <summary>
        /// Gets a string value representing the data type of an attribute value.
        /// </summary>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>String value representing the data type of an attribute value.</returns>
        static string GetType(JToken attributeValue)
        {
            if (attributeValue == null)
            {
                return "null";
            }

            switch (attributeValue.Type)
            {
                case JTokenType.Null:
                    return "null";
                case JTokenType.Undefined:
                    return "undefined";
                case JTokenType.Integer:
                case JTokenType.Float:
                    return "number";
                case JTokenType.Array:
                    return "array";
                case JTokenType.Boolean:
                    return "boolean";
                case JTokenType.String:
                    return "string";
                case JTokenType.Object:
                    return "object";
                default:
                    return "unknown";
            }
        }

        // #endregion
    }
}
