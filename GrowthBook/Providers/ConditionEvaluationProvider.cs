using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Providers
{
    internal sealed class ConditionEvaluationProvider : IConditionEvaluationProvider
    {
        /// <summary>
        /// The main function used to evaluate a condition.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy the condition.</returns>
        public bool EvalCondition(JToken attributes, JObject condition)
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

        /// <summary>
        /// Returns true if the attributes satisfy any of the conditions.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy any of the conditions.</returns>
        private bool EvalOr(JToken attributes, JArray conditions)
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
        private bool EvalAnd(JToken attributes, JArray conditions)
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
        /// Checks to see if a condition value matches an attribute value.
        /// </summary>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if the condition value matches the attribute value.</returns>
        private bool EvalConditionValue(JToken conditionValue, JToken attributeValue)
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
        private bool ElemMatch(JObject condition, JToken attributeVaue)
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
        
        /// <summary>
        /// A switch that handles all the possible operators.
        /// </summary>
        /// <param name="op">The operator to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <returns></returns>
        private bool EvalOperatorCondition(string op, JToken attributeValue, JToken conditionValue)
        {
            if (op == "$eq")
            {
                return conditionValue.Equals(attributeValue);
            }
            if (op == "$ne")
            {
                return !conditionValue.Equals(attributeValue);
            }

            var actualComparableValue = attributeValue as IComparable;

            if (op == "$lt")
            {
                return actualComparableValue is null || actualComparableValue?.CompareTo(conditionValue) < 0;
            }
            if (op == "$lte")
            {
                return actualComparableValue is null || actualComparableValue?.CompareTo(conditionValue) <= 0;
            }
            if (op == "$gt")
            {
                return actualComparableValue is null || actualComparableValue?.CompareTo(conditionValue) > 0;
            }
            if (op == "$gte")
            {
                return actualComparableValue is null || actualComparableValue?.CompareTo(conditionValue) >= 0;
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
                return CompareVersions(attributeValue, conditionValue, x => x == 0);
            }
            if (op == "$vne")
            {
                return CompareVersions(attributeValue, conditionValue, x => x != 0);
            }
            if (op == "$vlt")
            {
                return CompareVersions(attributeValue, conditionValue, x => x < 0);
            }
            if (op == "$vlte")
            {
                return CompareVersions(attributeValue, conditionValue, x => x <= 0);
            }
            if (op == "$vgt")
            {
                return CompareVersions(attributeValue, conditionValue, x => x > 0);
            }
            if (op == "$vgte")
            {
                return CompareVersions(attributeValue, conditionValue, x => x >= 0);
            }

            // TODO: Log the error for debug purposes?

            return false;
        }

        internal static string PaddedVersionString(string input)
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

        /// <summary>
        /// Checks to see if every key in the object is an operator.
        /// </summary>
        /// <param name="obj">The object to check.</param>
        /// <returns>True if every key in the object starts with $.</returns>
        private static bool IsOperatorObject(JObject obj)
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

        private static bool IsIn(JToken conditionValue, JToken actualValue)
        {
            if (actualValue?.Type == JTokenType.Array)
            {
                var conditionValues = new HashSet<JToken>(conditionValue);
                var actualValues = new HashSet<JToken>(actualValue);

                conditionValues.IntersectWith(actualValues);

                return conditionValues.Any();
            }
            else
            {
                if (conditionValue == actualValue)
                {
                    return true;
                }

                if (conditionValue is null || actualValue is null)
                {
                    return false;
                }

                return conditionValue.ToString().Contains(actualValue.ToString());
            }
        }

        private static bool CompareVersions(JToken left, JToken right, Func<int, bool> meetsComparison)
        {
            var leftValue = PaddedVersionString(left.ToString());
            var rightValue = PaddedVersionString(right.ToString());

            var comparisonResult = string.CompareOrdinal(leftValue, rightValue);

            return meetsComparison(comparisonResult);
        }

        private static JToken GetPath(JToken attributes, string key) => attributes.SelectToken(key);

        /// <summary>
        /// Gets a string value representing the data type of an attribute value.
        /// </summary>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>String value representing the data type of an attribute value.</returns>
        private static string GetType(JToken attributeValue)
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
    }
}
