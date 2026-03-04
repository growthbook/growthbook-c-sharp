using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GrowthBook.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Providers
{
    /// <inheritdoc cref="IConditionEvaluationProvider"/>
    internal sealed class ConditionEvaluationProvider : IConditionEvaluationProvider
    {
        private readonly ILogger<ConditionEvaluationProvider> _logger;

        /// <summary>
        /// Dictionary containing comparison operators and their evaluation functions.
        /// </summary>
        private static readonly Dictionary<string, Func<int, bool>> ComparisonOperators = new Dictionary<string, Func<int, bool>>
        {
            ["$lt"] = result => result < 0,
            ["$lte"] = result => result <= 0,
            ["$gt"] = result => result > 0,
            ["$gte"] = result => result >= 0
        };

        public ConditionEvaluationProvider(ILogger<ConditionEvaluationProvider> logger) => _logger = logger;

        /// <inheritdoc/>
        public bool EvalCondition(JToken attributes, JObject condition, JObject savedGroups = default)
        {
            _logger.LogInformation("Beginning to evaluate attributes based on the provided JSON condition");
            _logger.LogDebug("Attribute evaluation is based on the JSON condition \'{Condition}\'", condition);

            foreach (var innerCondition in condition.Properties())
            {
                switch(innerCondition.Name)
                {
                    case "$or":
                        if (!EvalOr(attributes, innerCondition.AsArray(), savedGroups))
                        {
                            return false;
                        }
                        break;
                    case "$nor":
                        if (EvalOr(attributes, innerCondition.AsArray(), savedGroups))
                        {
                            return false;
                        }
                        break;
                    case "$and":
                        if (!EvalAnd(attributes, innerCondition.AsArray(), savedGroups))
                        {
                            return false;
                        }
                        break;
                    case "$not":
                        if (EvalCondition(attributes, innerCondition.AsObject(), savedGroups))
                        {
                            return false;
                        }
                        break;
                    default:
                        if (!EvalConditionValue(innerCondition.Value, GetPath(attributes, innerCondition.Name), savedGroups))
                        {
                            return false;
                        }
                        break;
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
        private bool EvalOr(JToken attributes, JArray conditions, JObject savedGroups)
        {
            if (conditions.Count == 0)
            {
                _logger.LogDebug("No conditions found within the provided 'or' evaluation, skipping");
                return true;
            }

            _logger.LogDebug("Evaluating all conditions within an 'or' context");

            foreach (JObject condition in conditions)
            {
                if (EvalCondition(attributes, condition, savedGroups))
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
        private bool EvalAnd(JToken attributes, JArray conditions, JObject savedGroups)
        {
            _logger.LogDebug("Evaluating all conditions within an 'and' context");

            foreach (JObject condition in conditions)
            {
                if (!EvalCondition(attributes, condition, savedGroups))
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
        private bool EvalConditionValue(JToken conditionValue, JToken attributeValue, JObject savedGroups)
        {
            _logger.LogDebug("Evaluating condition value \'{ConditionValue}\'", conditionValue);

            if (conditionValue.Type == JTokenType.Object)
            {
                JObject conditionObj = (JObject)conditionValue;

                if (IsOperatorObject(conditionObj))
                {
                    _logger.LogDebug("Evaluating all condition properties against the operator condition");

                    foreach (JProperty property in conditionObj.Properties())
                    {
                        if (!EvalOperatorCondition(property.Name, attributeValue, property.Value, savedGroups))
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
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if attributeValue is an array and at least one of the array items matches the condition.</returns>
        private bool ElemMatch(JObject condition, JToken attributeValue, JObject savedGroups)
        {
            if (attributeValue?.Type != JTokenType.Array)
            {
                _logger.LogDebug("Unable to match array elements with a non-array type of '{AttributeValueType}'", attributeValue?.Type);
                return false;
            }

            foreach (JToken elem in (JArray)attributeValue)
            {
                if (IsOperatorObject(condition) && EvalConditionValue(condition, elem, savedGroups))
                {
                    return true;
                }

                if (EvalCondition(elem, condition, savedGroups))
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
        private bool EvalOperatorCondition(string op, JToken attributeValue, JToken conditionValue, JObject savedGroups)
        {
            _logger.LogDebug("Evaluating operator condition \'{Op}\'", op);

            if (op == "$eq")
            {
                return conditionValue.Equals(attributeValue);
            }
            if (op == "$ne")
            {
                return !conditionValue.Equals(attributeValue);
            }

            // Handle comparison operators with a cleaner approach
            if (ComparisonOperators.TryGetValue(op, out var comparisonFunc))
            {
                return EvaluateComparison(attributeValue, conditionValue, comparisonFunc);
            }

            var actualComparableValue = attributeValue as IComparable;

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
            if (op == "$nregex")
            {
                try
                {
                    return !Regex.IsMatch(attributeValue?.ToString(), conditionValue?.ToString());
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
                return IsIn(conditionValue, attributeValue, savedGroups);
            }
            if (op == "$nin")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return !IsIn(conditionValue, attributeValue, savedGroups);
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
                    if (!attributeList.Any(x => EvalConditionValue(condition, x, savedGroups)))
                    {
                        return false;
                    }
                }

                return true;
            }

            if (op == "$elemMatch")
            {
                return ElemMatch((JObject)conditionValue, attributeValue, savedGroups);
            }
            if (op == "$size")
            {
                if (attributeValue?.Type != JTokenType.Array)
                {
                    return false;
                }

                return EvalConditionValue(conditionValue, ((JArray)attributeValue).Count, savedGroups);
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
                return !EvalConditionValue(conditionValue, attributeValue, savedGroups);
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
            if (op == "$inGroup")
            {
                if (attributeValue != null && conditionValue != null)
                {
                    var array = savedGroups[conditionValue.ToString()]?.AsArray() ?? new JArray();

                    return IsIn(array, attributeValue, savedGroups);
                }
            }
            if (op == "$notInGroup")
            {
                if (attributeValue != null && conditionValue != null)
                {
                    var array = savedGroups[conditionValue.ToString()]?.AsArray() ?? new JArray();

                    return !IsIn(array, attributeValue, savedGroups);
                }
            }

            _logger.LogWarning("Unable to handle unsupported operator condition \'{Op}\', failing the condition", op);

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
        private bool IsOperatorObject(JObject obj)
        {
            _logger.LogDebug("Checking whether the object is an operator object");

            foreach (JProperty property in obj.Properties())
            {
                if (!property.Name.StartsWith("$"))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsIn(JToken conditionValue, JToken actualValue, JObject savedGroups)
        {
            if (actualValue?.Type == JTokenType.Array)
            {
                _logger.LogDebug("Evaluating whether the specified value is in an array");

                var conditionValues = new HashSet<JToken>(conditionValue);
                var actualValues = new HashSet<JToken>(actualValue);

                conditionValues.IntersectWith(actualValues);

                return conditionValues.Any();
            }
            else if (conditionValue is JArray array)
            {
                return array.Any(x => x.Equals(actualValue));
            }
            else
            {
                _logger.LogDebug("Evaluating whether the specified value is equal to or contained within the actual value");

                if (conditionValue == actualValue)
                {
                    return true;
                }

                if (conditionValue.IsNullOrWhitespace() || actualValue.IsNullOrWhitespace())
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

        /// <summary>
        /// Evaluates a comparison operation between an attribute value and a condition value.
        /// Returns false if the attribute is null/missing, as null values should not satisfy any comparison.
        /// </summary>
        /// <param name="attributeValue">The attribute value to compare.</param>
        /// <param name="conditionValue">The condition value to compare against.</param>
        /// <param name="meetsComparison">Function that determines if the comparison result meets the criteria.</param>
        /// <returns>True if the comparison is satisfied, false if attribute is null or comparison fails.</returns>
        private bool EvaluateComparison(JToken attributeValue, JToken conditionValue, Func<int, bool> meetsComparison)
        {
            // Null/missing attributes should never satisfy comparison operators
            if (attributeValue.IsNull())
            {
                return false;
            }

            // Try to parse both values as DateTime first
            if (TryParseDateTimes(attributeValue, conditionValue, out var attrDate, out var condDate))
            {
                var dateComparisonResult = attrDate.CompareTo(condDate);
                return meetsComparison(dateComparisonResult);
            }

            // Try to parse both values as numbers
            if (TryParseNumbers(attributeValue, conditionValue, out var attrNumber, out var condNumber))
            {
                var numberComparisonResult = attrNumber.CompareTo(condNumber);
                return meetsComparison(numberComparisonResult);
            }

            // Fall back to string comparison
            var attrString = attributeValue.ToString();
            var condString = conditionValue.ToString();

            var stringComparisonResult = string.Compare(attrString, condString, StringComparison.Ordinal);
            return meetsComparison(stringComparisonResult);
        }

        /// <summary>
        /// Attempts to parse both values as DateTime objects for proper date comparison.
        /// </summary>
        private static bool TryParseDateTimes(JToken attributeValue, JToken conditionValue, out DateTime attrDate, out DateTime condDate)
        {
            attrDate = default;
            condDate = default;

            var attrString = attributeValue.ToString();
            var condString = conditionValue.ToString();

            // Try parsing both values as DateTime
            var attrParsed = DateTime.TryParse(attrString, null, DateTimeStyles.RoundtripKind, out attrDate);
            var condParsed = DateTime.TryParse(condString, null, DateTimeStyles.RoundtripKind, out condDate);

            return attrParsed && condParsed;
        }

        /// <summary>
        /// Attempts to parse both values as numeric values for proper numeric comparison.
        /// </summary>
        private static bool TryParseNumbers(JToken attributeValue, JToken conditionValue, out double attrNumber, out double condNumber)
        {
            attrNumber = default;
            condNumber = default;

            // First try to get numeric values from JToken types directly
            if (attributeValue.Type == JTokenType.Integer || attributeValue.Type == JTokenType.Float)
            {
                attrNumber = attributeValue.Value<double>();
            }
            else if (!double.TryParse(attributeValue.ToString(), out attrNumber))
            {
                return false;
            }

            if (conditionValue.Type == JTokenType.Integer || conditionValue.Type == JTokenType.Float)
            {
                condNumber = conditionValue.Value<double>();
            }
            else if (!double.TryParse(conditionValue.ToString(), out condNumber))
            {
                return false;
            }

            return true;
        }
    }
}
