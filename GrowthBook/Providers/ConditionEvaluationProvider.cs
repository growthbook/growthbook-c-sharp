using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GrowthBook.Extensions;
using Microsoft.Extensions.Logging;

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
        public bool EvalCondition(JsonNode? attributes, JsonNode? condition, JsonObject? savedGroups = default)
        {
            _logger.LogInformation("Beginning to evaluate attributes based on the provided JSON condition");
            _logger.LogDebug("Attribute evaluation is based on the JSON condition \'{Condition}\'", condition);
            if (condition is not JsonObject objCondition)
                return false;

            foreach (var innerCondition in objCondition)
            {
                string name = innerCondition.Key;
                JsonNode? value = innerCondition.Value;
                switch (innerCondition.Key)
                {
                    case "$or":
                        if (value is JsonArray orArray && !EvalOr(attributes, orArray, savedGroups))
                            return false;
                        break;
                    case "$nor":
                        if (value is JsonArray norArray && EvalOr(attributes, norArray, savedGroups))
                            return false;
                        break;
                    case "$and":
                        if (value is JsonArray andArray && !EvalAnd(attributes, andArray, savedGroups))
                            return false;
                        break;
                    case "$not":
                        if (value is JsonObject notObj && EvalCondition(attributes, notObj, savedGroups))
                            return false;
                        break;

                    default:
                        if (!EvalConditionValue(innerCondition.Value, GetPath(attributes, innerCondition.Key), savedGroups))
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
        private bool EvalOr(JsonNode? attributes, JsonArray conditions, JsonObject? savedGroups)
        {
            if (conditions.Count == 0)
            {
                _logger.LogDebug("No conditions found within the provided 'or' evaluation, skipping");
                return true;
            }

            _logger.LogDebug("Evaluating all conditions within an 'or' context");

            foreach (JsonNode? condition in conditions)
            {
                if (EvalCondition(attributes, condition?.AsObject(), savedGroups))
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
        private bool EvalAnd(JsonNode? attributes, JsonArray conditions, JsonObject? savedGroups)
        {
            _logger.LogDebug("Evaluating all conditions within an 'and' context");

            foreach (JsonNode? condition in conditions)
            {
                if (!EvalCondition(attributes, condition?.AsObject(), savedGroups))
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
        private bool EvalConditionValue(JsonNode? conditionValue, JsonNode? attributeValue, JsonObject? savedGroups)
        {
            _logger.LogDebug("Evaluating condition value \'{ConditionValue}\'", conditionValue);

            if (conditionValue is JsonObject conditionObj)
            {

                if (IsOperatorObject(conditionObj))
                {
                    _logger.LogDebug("Evaluating all condition properties against the operator condition");

                    foreach (var property in conditionObj)
                    {
                        if (!EvalOperatorCondition(property.Key, attributeValue, property.Value, savedGroups))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            return JsonNode.DeepEquals(conditionValue, attributeValue);
        }

        /// <summary>
        /// Checks if attributeValue is an array, and if so at least one of the array items must match the condition.
        /// </summary>
        /// <param name="condition">The condition to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if attributeValue is an array and at least one of the array items matches the condition.</returns>
        private bool ElemMatch(JsonObject condition, JsonNode? attributeValue, JsonObject? savedGroups)
        {
            if (attributeValue is not JsonArray attributeArray)
            {
                _logger.LogDebug("Unable to match array elements with a non-array type of '{AttributeValueType}'", attributeValue?.GetType());
                return false;
            }

            foreach (JsonNode? elem in attributeArray)
            {
                if (elem is null) continue;

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
        private bool EvalOperatorCondition(string op, JsonNode? attributeValue, JsonNode? conditionValue, JsonObject? savedGroups)
        {
            _logger.LogDebug("Evaluating operator condition \'{Op}\'", op);

            if (op == "$eq")
            {
                return JsonNodeEquals(attributeValue, conditionValue);
            }
            if (op == "$ne")
            {
                return !JsonNodeEquals(attributeValue, conditionValue);
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
                    if (attributeValue == null || conditionValue == null)
                    {
                        return false;
                    }
                    return Regex.IsMatch(attributeValue.ToString(), conditionValue.ToString());
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
                    if (attributeValue == null || conditionValue == null)
                    {
                        return false;
                    }
                    return !Regex.IsMatch(attributeValue.ToString(), conditionValue.ToString());
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            if (op == "$in")
            {
                if (conditionValue is not JsonArray arr)
                {
                    return false;
                }
                return IsIn(conditionValue, attributeValue, savedGroups);
            }
            if (op == "$nin")
            {
                if (conditionValue is not JsonArray arr)
                {
                    return false;
                }
                return !IsIn(conditionValue, attributeValue, savedGroups);
            }
            if (op == "$all")
            {
                if (conditionValue is not JsonArray condList || attributeValue is not JsonArray attrList)
                    return false;

                foreach (var cond in condList)
                {
                    if (!attrList.Any(x => EvalConditionValue(cond, x, savedGroups)))
                        return false;
                }

                return true;
            }

            if (op == "$elemMatch")
            {
                if (conditionValue is JsonObject condObj)
                    return ElemMatch(condObj, attributeValue, savedGroups);
                return false;
            }
            if (op == "$size")
            {
                if (attributeValue is not JsonArray arr) return false;

                return EvalConditionValue(conditionValue, arr.Count, savedGroups);
            }
            if (op == "$exists")
            {
                var value = conditionValue?.GetValue<bool>() ?? false;
                return value ? attributeValue != null : attributeValue == null;
            }
            if (op == "$type")
            {
                return GetType(attributeValue) == conditionValue?.ToString();
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
                    var array = savedGroups?[conditionValue.ToString()] as JsonArray ?? new JsonArray();

                    return IsIn(array, attributeValue, savedGroups);
                }
            }
            if (op == "$notInGroup")
            {
                if (attributeValue != null && conditionValue != null)
                {
                    var array = savedGroups?[conditionValue.ToString()] as JsonArray ?? new JsonArray();

                    return !IsIn(array, attributeValue, savedGroups);
                }
            }

            _logger.LogWarning("Unable to handle unsupported operator condition \'{Op}\', failing the condition", op);

            return false;
        }

        internal static string PaddedVersionString(string? input)
        {
            // Remove build info and leading `v` if any
            // Split version into parts (both core version numbers and pre-release tags)
            // "v1.2.3-rc.1+build123" -> ["1","2","3","rc","1"]

            var trimmedVersion = Regex.Replace(input ?? string.Empty, @"(^v|\+.*$)", string.Empty);
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
        private bool IsOperatorObject(JsonObject obj)
        {
            _logger.LogDebug("Checking whether the object is an operator object");

            foreach (var property in obj)
            {
                if (!property.Key.StartsWith("$"))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsIn(JsonNode conditionValue, JsonNode? actualValue, JsonObject? savedGroups)
        {
            if (actualValue is JsonArray actualArray)
            {
                _logger.LogDebug("Evaluating whether the specified value is in an array");

                        if (conditionValue is not JsonArray conditionArray) return false;


                return actualArray.Any(actualItem =>
                conditionArray.Any(conditionItem => JsonNode.DeepEquals(actualItem, conditionItem)));
            }
            else if (conditionValue is JsonArray conditionArray)
            {
                return conditionArray.Any(x => x?.Equals(actualValue) == true);
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

                return conditionValue.ToString().Contains(actualValue?.ToString() ?? string.Empty);
            }
        }

        private static bool CompareVersions(JsonNode? left, JsonNode? right, Func<int, bool> meetsComparison)
        {
            var leftValue = PaddedVersionString(left?.ToString());
            var rightValue = PaddedVersionString(right?.ToString());

            var comparisonResult = string.CompareOrdinal(leftValue, rightValue);

            return meetsComparison(comparisonResult);
        }

        private static JsonNode? GetPath(JsonNode? attributes, string key)
        {
            if (attributes == null || string.IsNullOrEmpty(key))
                return null;

            var parts = key.Split('.');
            JsonNode? current = attributes;

            foreach (var part in parts)
            {
                if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var next))
                {
                    current = next;
                }
                else
                {
                    return null;
                }
            }

            return current;
        }
        /// <summary>
        /// Gets a string value representing the data type of an attribute value.
        /// </summary>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>String value representing the data type of an attribute value.</returns>
        private static string GetType(JsonNode? attributeValue)
        {
            if (attributeValue == null)
                return "null";

            return attributeValue switch
            {
                JsonValue _ => GetJsonValueType((JsonValue)attributeValue),
                JsonArray => "array",
                JsonObject => "object",
                _ => "unknown"
            };
        }

        private static string GetJsonValueType(JsonValue value)
        {
            var raw = value.GetValue<object>();
            return raw switch
            {
                null => "null",
                int or long or float or double or decimal => "number",
                bool => "boolean",
                string => "string",
                _ => "unknown"
            };
        }

        /// <summary>
        /// Evaluates a comparison operation between an attribute value and a condition value.
        /// Returns false if the attribute is null/missing, as null values should not satisfy any comparison.
        /// </summary>
        /// <param name="attributeValue">The attribute value to compare.</param>
        /// <param name="conditionValue">The condition value to compare against.</param>
        /// <param name="meetsComparison">Function that determines if the comparison result meets the criteria.</param>
        /// <returns>True if the comparison is satisfied, false if attribute is null or comparison fails.</returns>
        private bool EvaluateComparison(JsonNode? attributeValue, JsonNode? conditionValue, Func<int, bool> meetsComparison)
        {
            // Null/missing attributes should never satisfy comparison operators
            if (attributeValue == null)
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
            var attrString = attributeValue?.ToString();
            var condString = conditionValue?.ToString();

            var stringComparisonResult = string.Compare(attrString, condString, StringComparison.Ordinal);
            return meetsComparison(stringComparisonResult);
        }

        /// <summary>
        /// Attempts to parse both values as DateTime objects for proper date comparison.
        /// </summary>
        private static bool TryParseDateTimes(JsonNode? attributeValue, JsonNode? conditionValue, out DateTime attrDate, out DateTime condDate)
        {
            attrDate = default;
            condDate = default;

            var attrString = attributeValue?.ToString();
            var condString = conditionValue?.ToString();

            // Try parsing both values as DateTime
            var attrParsed = DateTime.TryParse(attrString, null, DateTimeStyles.RoundtripKind, out attrDate);
            var condParsed = DateTime.TryParse(condString, null, DateTimeStyles.RoundtripKind, out condDate);

            return attrParsed && condParsed;
        }

        /// <summary>
        /// Attempts to parse both values as numeric values for proper numeric comparison.
        /// </summary>
        private static bool TryParseNumbers(JsonNode? attributeValue, JsonNode? conditionValue, out double attrNumber, out double condNumber)
        {
            attrNumber = default;
            condNumber = default;

            // Парсимо attributeValue
            if (attributeValue is JsonValue attrValue)
            {
                if (!attrValue.TryGetValue<double>(out attrNumber))
                {
                    // якщо не вдалося, пробуємо через рядок
                    if (!double.TryParse(attributeValue.ToString(), out attrNumber))
                        return false;
                }
            }
            else if (!double.TryParse(attributeValue?.ToString(), out attrNumber))
            {
                return false;
            }

            // Парсимо conditionValue
            if (conditionValue is JsonValue condValue)
            {
                if (!condValue.TryGetValue<double>(out condNumber))
                {
                    if (!double.TryParse(conditionValue.ToString(), out condNumber))
                        return false;
                }
            }
            else if (!double.TryParse(conditionValue?.ToString(), out condNumber))
            {
                return false;
            }

            return true;
        }


        private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
        {
            if (a == null || b == null) return false;

            return a.ToJsonString() == b.ToJsonString(); // просте порівняння
        }
    }
}
