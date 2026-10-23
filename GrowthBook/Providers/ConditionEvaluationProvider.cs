using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using GrowthBook.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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

        /// <summary>
        /// State shared by one evaluation: the saved groups and the <c>$savedGroup</c> ids already being resolved, to stop cycles.
        /// </summary>
        private sealed class EvalContext
        {
            public EvalContext(JObject savedGroups, HashSet<string> visited)
            {
                SavedGroups = savedGroups;
                Visited = visited;
            }

            public JObject SavedGroups { get; }
            public HashSet<string> Visited { get; }
        }

        public ConditionEvaluationProvider(ILogger<ConditionEvaluationProvider> logger) => _logger = logger;

        /// <inheritdoc/>
        public bool EvalCondition(JToken attributes, JObject condition, JObject savedGroups = default)
        {
            _logger.LogInformation("Beginning to evaluate attributes based on the provided JSON condition");

            return EvalCondition(attributes, condition, new EvalContext(savedGroups, new HashSet<string>()));
        }

        private bool EvalCondition(JToken attributes, JObject condition, EvalContext ctx)
        {
            _logger.LogDebug("Attribute evaluation is based on the JSON condition \'{Condition}\'", condition);

            foreach (var innerCondition in condition.Properties())
            {
                switch(innerCondition.Name)
                {
                    case "$or":
                        if (!EvalOr(attributes, innerCondition.AsArray(), ctx))
                        {
                            return false;
                        }
                        break;
                    case "$nor":
                        if (EvalOr(attributes, innerCondition.AsArray(), ctx))
                        {
                            return false;
                        }
                        break;
                    case "$and":
                        if (!EvalAnd(attributes, innerCondition.AsArray(), ctx))
                        {
                            return false;
                        }
                        break;
                    case "$not":
                        if (EvalCondition(attributes, innerCondition.AsObject(), ctx))
                        {
                            return false;
                        }
                        break;
                    case "$savedGroup":
                        if (!EvalSavedGroup(attributes, innerCondition.Value, ctx))
                        {
                            return false;
                        }
                        break;
                    default:
                        if (!EvalConditionValue(innerCondition.Value, GetPath(attributes, innerCondition.Name), ctx))
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
        private bool EvalOr(JToken attributes, JArray conditions, EvalContext ctx)
        {
            if (conditions.Count == 0)
            {
                _logger.LogDebug("No conditions found within the provided 'or' evaluation, skipping");
                return true;
            }

            _logger.LogDebug("Evaluating all conditions within an 'or' context");

            foreach (JObject condition in conditions)
            {
                if (EvalCondition(attributes, condition, ctx))
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
        private bool EvalAnd(JToken attributes, JArray conditions, EvalContext ctx)
        {
            _logger.LogDebug("Evaluating all conditions within an 'and' context");

            foreach (JObject condition in conditions)
            {
                if (!EvalCondition(attributes, condition, ctx))
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
        private bool EvalConditionValue(JToken conditionValue, JToken attributeValue, EvalContext ctx)
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
                        if (!EvalOperatorCondition(property.Name, attributeValue, property.Value, ctx))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }

            return ValuesEqual(conditionValue, attributeValue);
        }

        /// <summary>
        /// Resolves a <c>$savedGroup</c> reference. Anything unrecognized matches nobody.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="reference">The reference, an object with an <c>id</c> and an optional <c>attributeKey</c> override.</param>
        /// <returns>True if the attributes are in the referenced group.</returns>
        private bool EvalSavedGroup(JToken attributes, JToken reference, EvalContext ctx)
        {
            if (!(reference is JObject referenceObj) || referenceObj["id"]?.Type != JTokenType.String)
            {
                return false;
            }

            var id = referenceObj["id"].ToString();

            if (ctx.Visited.Contains(id))
            {
                return false;
            }

            // Falling back to the entry's attribute would check a different population
            var attributeKey = referenceObj["attributeKey"];

            if (attributeKey != null && attributeKey.Type != JTokenType.String)
            {
                return false;
            }

            // Absent, or a legacy bare array
            if (!(ctx.SavedGroups?[id] is JObject entry))
            {
                return false;
            }

            var type = entry["type"]?.Type == JTokenType.String ? entry["type"].ToString() : null;

            if (type == "list")
            {
                // The override wins over the entry's own attribute
                var key = attributeKey ?? entry["attributeKey"];

                if (key?.Type != JTokenType.String || !(entry["values"] is JArray values))
                {
                    return false;
                }

                return IsIn(values, GetPath(attributes, key.ToString()));
            }

            if (type == "condition")
            {
                // A condition group has no single attribute, so any override is ignored
                if (!(entry["condition"] is JObject condition))
                {
                    return false;
                }

                // A copy, so sibling groups don't see each other's ids
                var visited = new HashSet<string>(ctx.Visited) { id };

                return EvalCondition(attributes, condition, new EvalContext(ctx.SavedGroups, visited));
            }

            // A group type added after this SDK was built
            return false;
        }

        /// <summary>
        /// Checks if attributeValue is an array, and if so at least one of the array items must match the condition.
        /// </summary>
        /// <param name="condition">The condition to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if attributeValue is an array and at least one of the array items matches the condition.</returns>
        private bool ElemMatch(JObject condition, JToken attributeValue, EvalContext ctx)
        {
            if (attributeValue?.Type != JTokenType.Array)
            {
                _logger.LogDebug("Unable to match array elements with a non-array type of '{AttributeValueType}'", attributeValue?.Type);
                return false;
            }

            foreach (JToken elem in (JArray)attributeValue)
            {
                if (IsOperatorObject(condition) && EvalConditionValue(condition, elem, ctx))
                {
                    return true;
                }

                if (EvalCondition(elem, condition, ctx))
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
        private bool EvalOperatorCondition(string op, JToken attributeValue, JToken conditionValue, EvalContext ctx)
        {
            _logger.LogDebug("Evaluating operator condition \'{Op}\'", op);

            if (op == "$eq")
            {
                return ValuesEqual(conditionValue, attributeValue);
            }
            if (op == "$ne")
            {
                return !ValuesEqual(conditionValue, attributeValue);
            }

            // Handle comparison operators with a cleaner approach
            if (ComparisonOperators.TryGetValue(op, out var comparisonFunc))
            {
                return EvaluateComparison(attributeValue, conditionValue, comparisonFunc);
            }

            if (op == "$regex")
            {
                return EvaluateRegex(attributeValue, conditionValue, RegexOptions.None, negate: false);
            }
            if (op == "$regexi")
            {
                return EvaluateRegex(attributeValue, conditionValue, RegexOptions.IgnoreCase, negate: false);
            }
            if (op == "$nregex")
            {
                return EvaluateRegex(attributeValue, conditionValue, RegexOptions.None, negate: true);
            }
            if (op == "$in")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return IsIn(conditionValue, attributeValue);
            }
            if (op == "$ini")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return IsIn(conditionValue, attributeValue, StringComparison.OrdinalIgnoreCase);
            }
            if (op == "$nin")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return !IsIn(conditionValue, attributeValue);
            }
            if (op == "$nini")
            {
                if (conditionValue.Type != JTokenType.Array)
                {
                    return false;
                }
                return !IsIn(conditionValue, attributeValue, StringComparison.OrdinalIgnoreCase);
            }
            if (op == "$all")
            {
                return IsAll(conditionValue, attributeValue, ctx, stringComparison: null);
            }
            if (op == "$alli")
            {
                return IsAll(conditionValue, attributeValue, ctx, StringComparison.OrdinalIgnoreCase);
            }

            if (op == "$elemMatch")
            {
                return ElemMatch((JObject)conditionValue, attributeValue, ctx);
            }
            if (op == "$size")
            {
                if (attributeValue?.Type != JTokenType.Array)
                {
                    return false;
                }

                return EvalConditionValue(conditionValue, ((JArray)attributeValue).Count, ctx);
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
                return !EvalConditionValue(conditionValue, attributeValue, ctx);
            }
            if (op == "$veq")
            {
                return CompareVersions(attributeValue, conditionValue, x => x == 0, matchesOnTypeMismatch: false);
            }
            if (op == "$vne")
            {
                return CompareVersions(attributeValue, conditionValue, x => x != 0, matchesOnTypeMismatch: true);
            }
            if (op == "$vlt")
            {
                return CompareVersions(attributeValue, conditionValue, x => x < 0, matchesOnTypeMismatch: false);
            }
            if (op == "$vlte")
            {
                return CompareVersions(attributeValue, conditionValue, x => x <= 0, matchesOnTypeMismatch: false);
            }
            if (op == "$vgt")
            {
                return CompareVersions(attributeValue, conditionValue, x => x > 0, matchesOnTypeMismatch: false);
            }
            if (op == "$vgte")
            {
                return CompareVersions(attributeValue, conditionValue, x => x >= 0, matchesOnTypeMismatch: false);
            }
            if (op == "$inGroup")
            {
                var inGroupValues = GetSavedGroupValues(ctx.SavedGroups, conditionValue);

                // The null check sits outside the negation on purpose, so a group that could not be
                // resolved answers false for both operators rather than true for this one by default.
                return inGroupValues != null && IsIn(inGroupValues, attributeValue);
            }
            if (op == "$notInGroup")
            {
                var notInGroupValues = GetSavedGroupValues(ctx.SavedGroups, conditionValue);

                return notInGroupValues != null && !IsIn(notInGroupValues, attributeValue);
            }

            _logger.LogWarning("Unable to handle unsupported operator condition \'{Op}\', failing the condition", op);

            return false;
        }

        internal static string PaddedVersionString(string input)
        {
            // An empty version is version 0
            if (string.IsNullOrEmpty(input))
            {
                input = "0";
            }

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

        private static bool EvaluateRegex(JToken attributeValue, JToken conditionValue, RegexOptions options, bool negate)
        {
            if (attributeValue.IsNull() || conditionValue.IsNull())
            {
                return false;
            }

            // Only strings are matched. Any other type fails the regex, so its negation matches.
            if (!IsString(attributeValue))
            {
                return negate;
            }

            try
            {
                var isMatch = Regex.IsMatch(StringValue(attributeValue), conditionValue.ToString(), options);

                return negate ? !isMatch : isMatch;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves a saved group to the values it holds, mirroring the reference's
        /// <c>getSavedGroupArrayValues</c>.
        /// </summary>
        /// <remarks>
        /// Three outcomes, not two. A group nobody has defined is an empty one, and a user is
        /// legitimately not in it. A group that is defined but isn't a usable list - a bare string, a
        /// number, a JSON null, an object that isn't a list - cannot be resolved at all, and the SDK
        /// has nothing to say about membership either way. Null is that case, and the caller answers
        /// false for both operators rather than letting <c>$notInGroup</c> target everyone through a
        /// negation. Returning an empty array for it instead would do exactly that.
        /// </remarks>
        /// <returns>The group's values, or null when the group could not be resolved.</returns>
        private static JArray GetSavedGroupValues(JObject savedGroups, JToken groupId)
        {
            if (savedGroups is null || groupId is null)
            {
                return new JArray();
            }

            var entry = savedGroups[groupId.ToString()];

            // A key that isn't there at all is null here, which is the reference's `undefined` case.
            // A key present with a JSON null is a JValue instead, and falls through to the end as a
            // group that is defined but unusable - the same split the reference's truthiness check makes.
            if (entry is null)
            {
                return new JArray();
            }

            if (entry is JArray values)
            {
                return values;
            }

            // The other shape the reference accepts, alongside a bare array. The type is read as a
            // string only when it is one: casting an object or an array throws, and a malformed
            // group is unresolvable rather than a reason to abandon the evaluation.
            if (entry is JObject group && group["type"]?.Type == JTokenType.String && (string)group["type"] == "list")
            {
                return group["values"] as JArray;
            }

            return null;
        }

        private bool IsIn(JToken conditionValue, JToken actualValue, StringComparison? stringComparison = null)
        {
            if (actualValue?.Type == JTokenType.Array)
            {
                _logger.LogDebug("Evaluating whether the specified value is in an array");

                if (stringComparison is null && TryIntersectScalars(conditionValue, actualValue, out var intersects))
                {
                    return intersects;
                }

                // Pair by pair, so timestamps Json.NET read as dates still match strings
                return conditionValue.Any(condition => actualValue.Any(actual => TokensEqual(condition, actual, stringComparison)));
            }
            else if (conditionValue is JArray array)
            {
                return array.Any(x => TokensEqual(x, actualValue, stringComparison));
            }
            else
            {
                _logger.LogDebug("Evaluating whether the specified value is equal to or contained within the actual value");

                if (TokensEqual(conditionValue, actualValue, stringComparison))
                {
                    return true;
                }

                if (conditionValue.IsNullOrWhitespace() || actualValue.IsNullOrWhitespace())
                {
                    return false;
                }

                return conditionValue.ToString().IndexOf(actualValue.ToString(), stringComparison ?? StringComparison.Ordinal) >= 0;
            }
        }

        /// <summary>
        /// Checks whether two lists share a value using a hash lookup, which keeps large saved groups fast. Only
        /// handles lists of strings, numbers, booleans and nulls; returns false if either list holds anything else.
        /// </summary>
        private static bool TryIntersectScalars(JToken conditionValues, JToken actualValues, out bool intersects)
        {
            intersects = false;
            var keys = new HashSet<(JTokenType Type, object Value)>();

            foreach (var condition in conditionValues)
            {
                if (!TryGetScalarKey(condition, out var key))
                {
                    return false;
                }

                keys.Add(key);
            }

            foreach (var actual in actualValues)
            {
                if (!TryGetScalarKey(actual, out var key))
                {
                    return false;
                }

                if (keys.Contains(key))
                {
                    intersects = true;
                    return true;
                }
            }

            return true;
        }

        /// <summary>
        /// Gets a hash key for a string, number, boolean or null that is equal exactly when <see cref="ValuesEqual"/>
        /// is: 1 and 1.0 share a key, but "1", 1 and true don't, and neither do distinct large integers.
        /// </summary>
        private static bool TryGetScalarKey(JToken token, out (JTokenType Type, object Value) key)
        {
            switch (token.Type)
            {
                case JTokenType.String:
                case JTokenType.Boolean:
                    key = (token.Type, ((JValue)token).Value);
                    return true;
                case JTokenType.Integer:
                    key = (JTokenType.Integer, IntegerValue(token));
                    return true;
                case JTokenType.Float:
                    var number = DoubleValue(token);

                    // A whole number below 2^53 equals exactly one integer, so it shares that integer's key (this also
                    // makes -0.0 and 0 the same). A larger one equals every integer that rounds to it, which a key
                    // can't express, so those lists are compared pair by pair.
                    if (Math.Floor(number) == number)
                    {
                        if (Math.Abs(number) >= MaxSafeInteger)
                        {
                            key = default;
                            return false;
                        }

                        key = (JTokenType.Integer, new BigInteger(number));
                        return true;
                    }

                    key = (JTokenType.Float, number);
                    return true;
                case JTokenType.Null:
                    key = (JTokenType.Null, null);
                    return true;
                default:
                    key = default;
                    return false;
            }
        }

        private bool IsAll(JToken conditionValue, JToken attributeValue, EvalContext ctx, StringComparison? stringComparison)
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
                if (stringComparison is null)
                {
                    if (!attributeList.Any(x => EvalConditionValue(condition, x, ctx)))
                    {
                        return false;
                    }
                }
                else if (!attributeList.Any(x => TokensEqual(condition, x, stringComparison)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TokensEqual(JToken left, JToken right, StringComparison? stringComparison)
        {
            if (stringComparison != null && left?.Type == JTokenType.String && right?.Type == JTokenType.String)
            {
                return string.Equals(left.ToString(), right.ToString(), stringComparison.Value);
            }

            return ValuesEqual(left, right);
        }

        /// <summary>
        /// Compares two versions. Both sides must be strings, or both numbers; anything else is a type mismatch.
        /// </summary>
        /// <param name="matchesOnTypeMismatch">The result for a type mismatch: true only for <c>$vne</c>, the negation of <c>$veq</c>.</param>
        private static bool CompareVersions(JToken left, JToken right, Func<int, bool> meetsComparison, bool matchesOnTypeMismatch)
        {
            if (!IsOrderable(left, right))
            {
                return matchesOnTypeMismatch;
            }

            var leftValue = PaddedVersionString(VersionText(left));
            var rightValue = PaddedVersionString(VersionText(right));

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
                case JTokenType.Date:
                    return "string";
                case JTokenType.Object:
                    return "object";
                default:
                    return "unknown";
            }
        }

        /// <summary>
        /// Evaluates a comparison operation between an attribute value and a condition value.
        /// Numbers compare numerically and strings compare as text. Any other pairing, including a
        /// missing or null attribute and booleans, fails the comparison.
        /// </summary>
        /// <param name="attributeValue">The attribute value to compare.</param>
        /// <param name="conditionValue">The condition value to compare against.</param>
        /// <param name="meetsComparison">Function that determines if the comparison result meets the criteria.</param>
        /// <returns>True if both values have the same orderable type and the comparison is satisfied.</returns>
        private static bool EvaluateComparison(JToken attributeValue, JToken conditionValue, Func<int, bool> meetsComparison)
        {
            (attributeValue, conditionValue) = AlignDates(attributeValue, conditionValue);

            if (!IsOrderable(attributeValue, conditionValue))
            {
                return false;
            }

            if (IsNumber(attributeValue))
            {
                return meetsComparison(CompareNumbers(attributeValue, conditionValue));
            }

            if (attributeValue.Type == JTokenType.Date && conditionValue.Type == JTokenType.Date)
            {
                return meetsComparison(Instant(attributeValue).CompareTo(Instant(conditionValue)));
            }

            return meetsComparison(string.CompareOrdinal(StringValue(attributeValue), StringValue(conditionValue)));
        }

        /// <summary>
        /// Returns true when both values are numbers, or both are strings.
        /// </summary>
        private static bool IsOrderable(JToken left, JToken right) =>
            (IsNumber(left) && IsNumber(right)) || (IsString(left) && IsString(right));

        private static bool IsNumber(JToken value) =>
            value?.Type == JTokenType.Integer || value?.Type == JTokenType.Float;

        // 2^53: above it, doubles can't represent every integer
        private const double MaxSafeInteger = 9007199254740992d;

        /// <summary>
        /// Compares two numbers. Two integers compare exactly, so large ids that round to the same double stay
        /// distinct; otherwise both compare as doubles, as in JavaScript.
        /// </summary>
        private static int CompareNumbers(JToken left, JToken right) =>
            left.Type == JTokenType.Integer && right.Type == JTokenType.Integer
                ? IntegerValue(left).CompareTo(IntegerValue(right))
                : DoubleValue(left).CompareTo(DoubleValue(right));

        // Json.NET holds an integer as a long, or as a BigInteger when it doesn't fit
        private static BigInteger IntegerValue(JToken value)
        {
            switch (((JValue)value).Value)
            {
                case BigInteger integer:
                    return integer;
                case ulong unsigned:
                    return unsigned;
                default:
                    return Convert.ToInt64(((JValue)value).Value, CultureInfo.InvariantCulture);
            }
        }

        private static double DoubleValue(JToken value) =>
            value.Type == JTokenType.Integer
                ? (double)IntegerValue(value)
                : Convert.ToDouble(((JValue)value).Value, CultureInfo.InvariantCulture);

        // Json.NET reads ISO 8601 date-time strings as dates by default, but they are still strings in the payload.
        private static bool IsString(JToken value) =>
            value?.Type == JTokenType.String || value?.Type == JTokenType.Date;

        /// <summary>
        /// Gets the text of a string value. A date read by Json.NET is written back in round-trip ISO 8601 format.
        /// </summary>
        private static string StringValue(JToken value)
        {
            if (value.Type == JTokenType.Date)
            {
                var date = ((JValue)value).Value;

                return date is DateTimeOffset offset
                    ? offset.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)
                    : ((DateTime)date).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        /// <summary>
        /// Compares two values for equality, comparing arrays and objects element by element. Numbers compare by
        /// value whether written as integers or decimals. Timestamps Json.NET
        /// read as dates are equal when they are the same instant, and equal a string holding the same timestamp.
        /// </summary>
        private static bool ValuesEqual(JToken left, JToken right)
        {
            (left, right) = AlignDates(left, right);

            if (left?.Type == JTokenType.Date && right?.Type == JTokenType.Date)
            {
                return Instant(left) == Instant(right);
            }

            // JSON has one number type, so 1 equals 1.0
            if (IsNumber(left) && IsNumber(right))
            {
                return CompareNumbers(left, right) == 0;
            }

            if (left is JArray leftArray && right is JArray rightArray)
            {
                return leftArray.Count == rightArray.Count && leftArray.Zip(rightArray, ValuesEqual).All(equal => equal);
            }

            if (left is JObject leftObject && right is JObject rightObject)
            {
                return leftObject.Count == rightObject.Count
                    && leftObject.Properties().All(property => rightObject.TryGetValue(property.Name, out var value) && ValuesEqual(property.Value, value));
            }

            return JToken.DeepEquals(left ?? JValue.CreateNull(), right ?? JValue.CreateNull());
        }

        /// <summary>
        /// Json.NET reads ISO 8601 strings as dates by default, which loses their original text. When only one side
        /// was read that way, reads the other side the same way so that equal timestamps compare as equal.
        /// </summary>
        private static (JToken Left, JToken Right) AlignDates(JToken left, JToken right)
        {
            if (left?.Type == JTokenType.Date && right?.Type == JTokenType.String)
            {
                return (left, ReadAsDate(right, left));
            }

            if (right?.Type == JTokenType.Date && left?.Type == JTokenType.String)
            {
                return (ReadAsDate(left, right), right);
            }

            return (left, right);
        }

        /// <summary>
        /// Reads a string the way Json.NET read the given date, returning the string unchanged if it isn't a timestamp.
        /// </summary>
        private static JToken ReadAsDate(JToken text, JToken date)
        {
            var dateParseHandling = ((JValue)date).Value is DateTimeOffset
                ? DateParseHandling.DateTimeOffset
                : DateParseHandling.DateTime;

            using (var reader = new JsonTextReader(new StringReader(JsonConvert.ToString(text.ToString()))) { DateParseHandling = dateParseHandling })
            {
                var token = JToken.ReadFrom(reader);

                return token.Type == JTokenType.Date ? token : text;
            }
        }

        // A date without an offset is treated as local time, the same way for both sides of a comparison
        private static DateTimeOffset Instant(JToken date) =>
            ((JValue)date).Value is DateTimeOffset offset ? offset : new DateTimeOffset((DateTime)((JValue)date).Value);

        /// <summary>
        /// Gets the text of a version, writing numbers the way JavaScript does (2.0 is "2").
        /// </summary>
        private static string VersionText(JToken value) =>
            IsNumber(value)
                ? DoubleValue(value).ToString("R", CultureInfo.InvariantCulture)
                : StringValue(value);
    }
}
