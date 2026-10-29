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
        /// Keys a condition can have that aren't attributes, besides <c>$not</c>, which is also an operator on a value.
        /// </summary>
        private static readonly HashSet<string> ConditionOperators = new HashSet<string> { "$or", "$nor", "$and", "$savedGroup" };

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

            // Unknown (null) fails, since the result depends on something this SDK doesn't support
            return EvalCondition(attributes, condition, new EvalContext(savedGroups, new HashSet<string>())) == true;
        }

        /// <summary>
        /// Evaluates a condition. Null means the result depends on an operator or saved group type this SDK
        /// doesn't support, so negating it with <c>$not</c> or <c>$nor</c> can't make it pass.
        /// </summary>
        private bool? EvalCondition(JToken attributes, JObject condition, EvalContext ctx)
        {
            _logger.LogDebug("Attribute evaluation is based on the JSON condition \'{Condition}\'", condition);

            bool? result = true;

            foreach (var innerCondition in condition.Properties())
            {
                bool? r;

                switch(innerCondition.Name)
                {
                    case "$or":
                        r = EvalOr(attributes, innerCondition.AsArray(), ctx);
                        break;
                    case "$nor":
                        r = !EvalOr(attributes, innerCondition.AsArray(), ctx);
                        break;
                    case "$and":
                        r = EvalAnd(attributes, innerCondition.AsArray(), ctx);
                        break;
                    case "$not":
                        r = !EvalCondition(attributes, innerCondition.AsObject(), ctx);
                        break;
                    case "$savedGroup":
                        r = EvalSavedGroup(attributes, innerCondition.Value, ctx);
                        break;
                    default:
                        r = EvalConditionValue(innerCondition.Value, GetPath(attributes, innerCondition.Name), ctx);
                        break;
                }

                // One failing entry decides it, wherever an unsupported one is
                if (r == false)
                {
                    return false;
                }

                if (r == null)
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns true if the attributes satisfy any of the conditions.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy any of the conditions.</returns>
        private bool? EvalOr(JToken attributes, JArray conditions, EvalContext ctx)
        {
            if (conditions.Count == 0)
            {
                _logger.LogDebug("No conditions found within the provided 'or' evaluation, skipping");
                return true;
            }

            _logger.LogDebug("Evaluating all conditions within an 'or' context");

            bool? result = false;

            foreach (JObject condition in conditions)
            {
                var r = EvalCondition(attributes, condition, ctx);

                if (r == true)
                {
                    return true;
                }

                if (r == null)
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns true if the attributes satisfy all of the conditions.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy all of the conditions.</returns>
        private bool? EvalAnd(JToken attributes, JArray conditions, EvalContext ctx)
        {
            _logger.LogDebug("Evaluating all conditions within an 'and' context");

            bool? result = true;

            foreach (JObject condition in conditions)
            {
                var r = EvalCondition(attributes, condition, ctx);

                if (r == false)
                {
                    return false;
                }

                if (r == null)
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Checks to see if a condition value matches an attribute value.
        /// </summary>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if the condition value matches the attribute value.</returns>
        private bool? EvalConditionValue(JToken conditionValue, JToken attributeValue, EvalContext ctx)
        {
            _logger.LogDebug("Evaluating condition value \'{ConditionValue}\'", conditionValue);

            if (conditionValue.Type == JTokenType.Object)
            {
                JObject conditionObj = (JObject)conditionValue;

                if (IsOperatorObject(conditionObj))
                {
                    _logger.LogDebug("Evaluating all condition properties against the operator condition");

                    bool? result = true;

                    foreach (JProperty property in conditionObj.Properties())
                    {
                        var r = EvalOperatorCondition(property.Name, attributeValue, property.Value, ctx);

                        if (r == false)
                        {
                            return false;
                        }

                        if (r == null)
                        {
                            result = null;
                        }
                    }

                    return result;
                }
            }

            return ValuesEqual(conditionValue, attributeValue);
        }

        /// <summary>
        /// The kinds of entry a <c>$savedGroup</c> reference can resolve to.
        /// </summary>
        private enum SavedGroupKind
        {
            /// <summary>Missing, malformed, or already being resolved: it matches nobody.</summary>
            Invalid,
            /// <summary>A list of values for one attribute.</summary>
            List,
            /// <summary>A condition on the attributes.</summary>
            Condition,
            /// <summary>A group type added after this SDK was built.</summary>
            Unknown
        }

        /// <summary>
        /// A <c>$savedGroup</c> reference resolved to its entry.
        /// </summary>
        private sealed class ResolvedSavedGroup
        {
            public SavedGroupKind Kind { get; set; }
            public string AttributeKey { get; set; }
            public JArray Values { get; set; }
            public JObject Condition { get; set; }
            /// <summary>The context for evaluating a condition group, with this group's id marked as visited.</summary>
            public EvalContext ConditionContext { get; set; }
        }

        private static ResolvedSavedGroup ResolveSavedGroup(JToken reference, EvalContext ctx)
        {
            var invalid = new ResolvedSavedGroup { Kind = SavedGroupKind.Invalid };

            if (!(reference is JObject referenceObj) || referenceObj["id"]?.Type != JTokenType.String)
            {
                return invalid;
            }

            var id = referenceObj["id"].ToString();

            if (ctx.Visited.Contains(id))
            {
                return invalid;
            }

            // Falling back to the entry's attribute would check a different population
            var attributeKey = referenceObj["attributeKey"];

            if (attributeKey != null && attributeKey.Type != JTokenType.String)
            {
                return invalid;
            }

            // Absent, or a legacy bare array
            if (!(ctx.SavedGroups?[id] is JObject entry))
            {
                return invalid;
            }

            var type = entry["type"]?.Type == JTokenType.String ? entry["type"].ToString() : null;

            if (type == "list")
            {
                // The override wins over the entry's own attribute
                var key = attributeKey ?? entry["attributeKey"];

                if (key?.Type != JTokenType.String || !(entry["values"] is JArray values))
                {
                    return invalid;
                }

                return new ResolvedSavedGroup { Kind = SavedGroupKind.List, AttributeKey = key.ToString(), Values = values };
            }

            if (type == "condition")
            {
                // A condition group has no single attribute, so any override is ignored
                if (!(entry["condition"] is JObject condition))
                {
                    return invalid;
                }

                // A copy, so sibling groups don't see each other's ids
                var visited = new HashSet<string>(ctx.Visited) { id };

                return new ResolvedSavedGroup
                {
                    Kind = SavedGroupKind.Condition,
                    Condition = condition,
                    ConditionContext = new EvalContext(ctx.SavedGroups, visited)
                };
            }

            return new ResolvedSavedGroup { Kind = SavedGroupKind.Unknown };
        }

        /// <summary>
        /// Resolves a <c>$savedGroup</c> reference. Anything unrecognized matches nobody.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="reference">The reference, an object with an <c>id</c> and an optional <c>attributeKey</c> override.</param>
        /// <returns>True if the attributes are in the referenced group, or null for a group type this SDK doesn't support.</returns>
        private bool? EvalSavedGroup(JToken attributes, JToken reference, EvalContext ctx)
        {
            var group = ResolveSavedGroup(reference, ctx);

            switch (group.Kind)
            {
                case SavedGroupKind.List:
                    return IsIn(group.Values, GetPath(attributes, group.AttributeKey));
                case SavedGroupKind.Condition:
                    return EvalCondition(attributes, group.Condition, group.ConditionContext);
                case SavedGroupKind.Unknown:
                    // A client SDK may support it
                    return null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Checks if attributeValue is an array, and if so at least one of the array items must match the expected value.
        /// </summary>
        /// <param name="expected">The <c>$elemMatch</c> value: operators or a condition, usually an object.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <returns>True if attributeValue is an array and at least one of the array items matches.</returns>
        private bool? ElemMatch(JToken expected, JToken attributeValue, EvalContext ctx)
        {
            if (attributeValue?.Type != JTokenType.Array)
            {
                _logger.LogDebug("Unable to match array elements with a non-array type of '{AttributeValueType}'", attributeValue?.Type);
                return false;
            }

            var check = GetElemMatchCheck(expected, ctx);

            bool? result = false;

            foreach (JToken elem in (JArray)attributeValue)
            {
                // Only skip null elements; falsy values like 0, "" and false must still be tested
                if (elem.IsNull())
                {
                    continue;
                }

                var r = check(elem);

                if (r == true)
                {
                    return true;
                }

                if (r == null)
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Builds the test for each array element, reading the value the way the JS SDK does with
        /// <c>Object.keys</c>.
        /// </summary>
        private Func<JToken, bool?> GetElemMatchCheck(JToken expected, EvalContext ctx)
        {
            if (expected.IsNull())
            {
                throw new ArgumentException("$elemMatch needs a value to match array elements against");
            }

            if (expected is JObject condition)
            {
                // Operators test each element itself. A condition, including one that only uses
                // $or, $and, $nor, or $savedGroup, tests each element's fields.
                if (IsOperatorObject(condition) && !condition.Properties().Any(x => ConditionOperators.Contains(x.Name)))
                {
                    return elem => EvalConditionValue(condition, elem, ctx);
                }

                return elem => EvalCondition(elem, condition, ctx);
            }

            // An array or a non-empty string has index keys, so it is a condition on each element's "0", "1", ...
            // fields. Anything else has no keys, so each element is compared with the value itself.
            if (expected is JArray array)
            {
                var indexed = IndexedCondition(array);
                return elem => EvalCondition(elem, indexed, ctx);
            }

            // A timestamp Json.NET read as a date is still the string the payload holds
            if (IsString(expected) && StringValue(expected).Length > 0)
            {
                var indexed = IndexedCondition(StringValue(expected).Select(x => (JToken)x.ToString()));
                return elem => EvalCondition(elem, indexed, ctx);
            }

            return elem => EvalConditionValue(expected, elem, ctx);
        }

        private static JObject IndexedCondition(IEnumerable<JToken> values)
        {
            var condition = new JObject();
            var index = 0;

            foreach (var value in values)
            {
                condition[(index++).ToString(CultureInfo.InvariantCulture)] = value;
            }

            return condition;
        }

        /// <summary>
        /// A switch that handles all the possible operators.
        /// </summary>
        /// <param name="op">The operator to check.</param>
        /// <param name="attributeValue">The attribute value to check.</param>
        /// <param name="conditionValue">The condition value to check.</param>
        /// <returns>The result, or null for an operator this SDK doesn't support.</returns>
        private bool? EvalOperatorCondition(string op, JToken attributeValue, JToken conditionValue, EvalContext ctx)
        {
            _logger.LogDebug("Evaluating operator condition \'{Op}\'", op);

            if (op == "$eq")
            {
                return OperatorEquals(conditionValue, attributeValue);
            }
            if (op == "$ne")
            {
                return !OperatorEquals(conditionValue, attributeValue);
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
                return ElemMatch(conditionValue, attributeValue, ctx);
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
                if (IsNewGroupType(ctx.SavedGroups, conditionValue))
                {
                    return null;
                }

                var inGroupValues = GetSavedGroupValues(ctx.SavedGroups, conditionValue);

                // The null check sits outside the negation on purpose, so a group that could not be
                // resolved answers false for both operators rather than true for this one by default.
                return inGroupValues != null && IsIn(inGroupValues, attributeValue);
            }
            if (op == "$notInGroup")
            {
                if (IsNewGroupType(ctx.SavedGroups, conditionValue))
                {
                    return null;
                }

                var notInGroupValues = GetSavedGroupValues(ctx.SavedGroups, conditionValue);

                return notInGroupValues != null && !IsIn(notInGroupValues, attributeValue);
            }

            // Keys this SDK knows, but on a condition rather than on a value, where they never match
            if (ConditionOperators.Contains(op))
            {
                return false;
            }

            _logger.LogWarning("Unable to handle unsupported operator condition \'{Op}\', failing the condition", op);

            return null;
        }

        /// <summary>
        /// Partially evaluates a condition when only some attributes are known, for example on a server.
        /// Attributes missing from <paramref name="attributes"/> are unknown.
        /// </summary>
        /// <returns>
        /// A boolean token once the result can't depend on the unknown attributes, otherwise the condition
        /// minus every branch that is already decided.
        /// </returns>
        internal JToken PruneCondition(JObject attributes, JObject condition, JObject savedGroups)
        {
            return PruneWithContext(attributes ?? new JObject(), condition, new EvalContext(savedGroups ?? new JObject(), new HashSet<string>()));
        }

        /// <summary>
        /// Prunes the saved group with the given id.
        /// </summary>
        /// <returns>
        /// A boolean token once decided, the group's condition minus every decided branch for a condition
        /// group that isn't, or null for a list group whose attribute is unknown or a group type this SDK
        /// doesn't support.
        /// </returns>
        internal JToken PruneSavedGroup(JObject attributes, string id, JObject savedGroups)
        {
            var ctx = new EvalContext(savedGroups ?? new JObject(), new HashSet<string>());

            return PruneSavedGroupReference(attributes ?? new JObject(), new JObject { ["id"] = id }, ctx, PruneWithContext);
        }

        private JToken PruneWithContext(JObject attributes, JObject condition, EvalContext ctx)
        {
            // One that throws, such as a malformed one, stays as written. Evaluating it fails as a whole,
            // so no part of it can be decided.
            try
            {
                return PruneEntries(attributes, condition, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to prune the condition, keeping it as written");
                return condition;
            }
        }

        /// <returns>A boolean token, or the condition with every decided entry removed.</returns>
        private JToken PruneEntries(JObject attributes, JObject condition, EvalContext ctx)
        {
            var output = new JObject();

            foreach (var property in condition.Properties())
            {
                var key = property.Name;

                // A boolean token, a pruned container, or null for an undecided leaf
                JToken r;

                switch (key)
                {
                    case "$or":
                    case "$nor":
                    case "$and":
                        r = PruneList(attributes, (JArray)property.Value, ctx, or: key != "$and");
                        break;
                    case "$not":
                        r = PruneEntries(attributes, (JObject)property.Value, ctx);
                        break;
                    case "$savedGroup":
                        r = PruneSavedGroupReference(attributes, property.Value, ctx, PruneEntries);

                        // Keep an undecided reference as is rather than inlining the group
                        if (!IsBoolean(r))
                        {
                            r = null;
                        }
                        break;
                    default:
                        // Null when the attribute is unknown, or the value uses an operator this SDK doesn't
                        // support, which the client's SDK may
                        r = IsKnown(attributes, key)
                            ? ToToken(EvalConditionValue(property.Value, GetPath(attributes, key), ctx))
                            : null;
                        break;
                }

                // $not and $nor pass when what they hold fails
                if ((key == "$not" || key == "$nor") && IsBoolean(r))
                {
                    r = !r.Value<bool>();
                }

                // One failing entry fails the whole condition, and a passing one is dropped
                if (IsBoolean(r) && !r.Value<bool>())
                {
                    return false;
                }

                if (r is null)
                {
                    output[key] = property.Value.DeepClone();
                }
                else if (!IsBoolean(r))
                {
                    output[key] = r;
                }
            }

            return output.Count > 0 ? output : (JToken)true;
        }

        /// <summary>
        /// Prunes an <c>$or</c> (or an <c>$and</c>) list.
        /// </summary>
        /// <returns>The conditions still undecided, or the list's result once there are none.</returns>
        private JToken PruneList(JObject attributes, JArray conditions, EvalContext ctx, bool or)
        {
            var output = new JArray();

            foreach (var condition in conditions)
            {
                var r = PruneEntries(attributes, (JObject)condition, ctx);

                // One passing condition decides an $or, and one failing condition an $and
                if (IsBoolean(r) && r.Value<bool>() == or)
                {
                    return or;
                }

                if (!IsBoolean(r))
                {
                    output.Add(r);
                }
            }

            if (output.Count > 0)
            {
                return output;
            }

            // Every condition failed an $or, or passed an $and. Like EvalOr, an empty $or passes.
            return or ? conditions.Count == 0 : true;
        }

        /// <summary>
        /// Prunes a <c>$savedGroup</c> reference with the given function for condition groups.
        /// </summary>
        /// <returns>A boolean token, a pruned condition, or null while it is undecided.</returns>
        private JToken PruneSavedGroupReference(JObject attributes, JToken reference, EvalContext ctx, Func<JObject, JObject, EvalContext, JToken> prune)
        {
            var group = ResolveSavedGroup(reference, ctx);

            switch (group.Kind)
            {
                case SavedGroupKind.List:
                    if (!IsKnown(attributes, group.AttributeKey))
                    {
                        return null;
                    }
                    return IsIn(group.Values, GetPath(attributes, group.AttributeKey));
                case SavedGroupKind.Condition:
                    return prune(attributes, group.Condition, group.ConditionContext);
                case SavedGroupKind.Unknown:
                    return null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A nested path is unknown, since the server may only have part of the object.
        /// </summary>
        private static bool IsKnown(JObject attributes, string path) => !path.Contains(".") && attributes.ContainsKey(path);

        private static bool IsBoolean(JToken token) => token?.Type == JTokenType.Boolean;

        private static JToken ToToken(bool? result) => result.HasValue ? new JValue(result.Value) : null;

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

            // A pattern that isn't a string can't be compiled, so it fails, as in the JS SDK
            if (!IsString(conditionValue))
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
        /// Checks whether a saved group is of a type added after this SDK was built.
        /// </summary>
        private static bool IsNewGroupType(JObject savedGroups, JToken groupId)
        {
            if (savedGroups is null || groupId is null || !(savedGroups[groupId.ToString()] is JObject group))
            {
                return false;
            }

            var type = group["type"]?.Type == JTokenType.String ? (string)group["type"] : null;

            return type != "list" && type != "condition";
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
                return conditionValue.Any(condition => actualValue.Any(actual => IsSameMember(condition, actual, stringComparison)));
            }
            else if (conditionValue is JArray array)
            {
                return array.Any(x => IsSameMember(x, actualValue, stringComparison));
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

        private bool? IsAll(JToken conditionValue, JToken attributeValue, EvalContext ctx, StringComparison? stringComparison)
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

            bool? result = true;

            foreach (JToken condition in conditionList)
            {
                if (stringComparison != null)
                {
                    if (!attributeList.Any(x => TokensEqual(condition, x, stringComparison)))
                    {
                        return false;
                    }

                    continue;
                }

                bool? passed = false;

                foreach (JToken attribute in attributeList)
                {
                    var r = EvalConditionValue(condition, attribute, ctx);

                    if (r == true)
                    {
                        passed = true;
                        break;
                    }

                    if (r == null)
                    {
                        passed = null;
                    }
                }

                if (passed == false)
                {
                    return false;
                }

                if (passed == null)
                {
                    result = null;
                }
            }

            return result;
        }

        /// <summary>
        /// Compares two list members. JavaScript checks membership with <c>Array.prototype.includes</c>, which
        /// matches an array or object only by identity, so one parsed separately never matches; other values
        /// compare as in <see cref="TokensEqual"/>.
        /// </summary>
        private static bool IsSameMember(JToken left, JToken right, StringComparison? stringComparison) =>
            !(left is JContainer) && !(right is JContainer) && TokensEqual(left, right, stringComparison);

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

        /// <summary>
        /// Gets the value at a dot-separated path, the way the JS SDK does: each segment is an object's own key or
        /// an array index. Returns null when any segment is missing.
        /// </summary>
        private static JToken GetPath(JToken attributes, string key)
        {
            var current = attributes;

            foreach (var segment in key.Split('.'))
            {
                if (current is JObject obj && obj.TryGetValue(segment, StringComparison.Ordinal, out var child))
                {
                    current = child;
                }
                else if (current is JArray array && IsArrayIndex(segment, out var index) && index < array.Count)
                {
                    current = array[index];
                }
                else
                {
                    return null;
                }
            }

            return current;
        }

        /// <summary>
        /// Whether a path segment is a canonical array index, the only form JavaScript reads as one.
        /// </summary>
        private static bool IsArrayIndex(string segment, out int index)
        {
            index = -1;

            if (segment.Length == 0 || (segment.Length > 1 && segment[0] == '0') || !segment.All(c => c >= '0' && c <= '9'))
            {
                return false;
            }

            return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        /// <summary>
        /// Equality for <c>$eq</c> and <c>$ne</c>. The JS SDK compares with <c>===</c>, so an array or object
        /// is never equal to anything, even one with the same contents.
        /// </summary>
        private static bool OperatorEquals(JToken left, JToken right) =>
            !(left is JContainer) && !(right is JContainer) && ValuesEqual(left, right);

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
        /// missing or null attribute and booleans, fails the comparison, as does NaN.
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
                // In JavaScript every ordering with NaN is false, while CompareTo orders NaN below other numbers
                if (double.IsNaN(DoubleValue(attributeValue)) || double.IsNaN(DoubleValue(conditionValue)))
                {
                    return false;
                }

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

        internal static double DoubleValue(JToken value) =>
            value.Type == JTokenType.Integer
                ? (double)IntegerValue(value)
                : Convert.ToDouble(((JValue)value).Value, CultureInfo.InvariantCulture);

        // Json.NET reads ISO 8601 date-time strings as dates by default, but they are still strings in the payload.
        private static bool IsString(JToken value) =>
            value?.Type == JTokenType.String || value?.Type == JTokenType.Date;

        /// <summary>
        /// Gets the text of a string value. A date read by Json.NET is written back in round-trip ISO 8601 format.
        /// </summary>
        internal static string StringValue(JToken value)
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
                ? JavaScriptNumberText(DoubleValue(value))
                : StringValue(value);

        /// <summary>
        /// Writes a number the way JavaScript's <c>String(number)</c> does (ECMAScript Number::toString): the
        /// shortest digits that read back as the same number, in plain decimal notation from 1e-7 up to 1e21 and in
        /// exponent notation outside it ("0.000002", "1e-7", "1e+21"), with -0 written as "0".
        /// </summary>
        /// <param name="value">The number to write.</param>
        /// <returns>The number's JavaScript text.</returns>
        internal static string JavaScriptNumberText(double value)
        {
            if (double.IsNaN(value))
            {
                return "NaN";
            }

            if (double.IsInfinity(value))
            {
                return value > 0 ? "Infinity" : "-Infinity";
            }

            if (value == 0)
            {
                return "0";
            }

            if (value < 0)
            {
                return "-" + JavaScriptNumberText(-value);
            }

            // "R" gives the shortest round-trip digits, as in "1.5", "2E-06" or "1.23E-18"
            var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
            var exponentIndex = roundTrip.IndexOf('E');
            var mantissa = exponentIndex < 0 ? roundTrip : roundTrip.Substring(0, exponentIndex);
            var exponent = exponentIndex < 0 ? 0 : int.Parse(roundTrip.Substring(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var pointIndex = mantissa.IndexOf('.');
            var integerDigits = pointIndex < 0 ? mantissa : mantissa.Substring(0, pointIndex);

            // The value is 0.digits × 10^n
            var digits = (integerDigits + (pointIndex < 0 ? "" : mantissa.Substring(pointIndex + 1))).TrimEnd('0');
            var n = integerDigits.Length + exponent;
            var significant = digits.TrimStart('0');
            n -= digits.Length - significant.Length;
            digits = significant;

            var k = digits.Length;

            if (k <= n && n <= 21)
            {
                return digits + new string('0', n - k);
            }

            if (0 < n && n <= 21)
            {
                return digits.Substring(0, n) + "." + digits.Substring(n);
            }

            if (-6 < n && n <= 0)
            {
                return "0." + new string('0', -n) + digits;
            }

            var exponentText = (n - 1 < 0 ? "-" : "+") + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture);

            return k == 1
                ? digits + "e" + exponentText
                : digits.Substring(0, 1) + "." + digits.Substring(1) + "e" + exponentText;
        }
    }
}
