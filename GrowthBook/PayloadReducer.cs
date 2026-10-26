using System;
using System.Collections.Generic;
using System.Linq;
using GrowthBook.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrowthBook
{
    /// <summary>
    /// Filters for <see cref="PayloadReducer.ReducePayload"/>. Each filter is a list, and any one of its values is enough.
    /// </summary>
    public sealed class PayloadFilters
    {
        /// <summary>
        /// Project ids.
        /// </summary>
        public IList<string> Projects { get; set; }

        /// <summary>
        /// Tags.
        /// </summary>
        public IList<string> Tags { get; set; }

        /// <summary>
        /// Values for each custom field, by field key.
        /// </summary>
        public IDictionary<string, IList<JToken>> CustomFields { get; set; }
    }

    /// <summary>
    /// How <see cref="PayloadReducer.ReducePayload"/> treats the prerequisites of what it keeps.
    /// </summary>
    public enum PrerequisiteFiltering
    {
        /// <summary>
        /// Keep the prerequisites of what is kept even when the filters remove them, so the client evaluates the
        /// same as with the full payload.
        /// </summary>
        Keep,

        /// <summary>
        /// Apply the filters to prerequisites too. Holdouts are always kept.
        /// </summary>
        Filter
    }

    /// <summary>
    /// Options for <see cref="PayloadReducer.ReducePayload"/>.
    /// </summary>
    public sealed class ReducePayloadOptions
    {
        /// <summary>
        /// Keep only the features and auto experiments that match every filter.
        /// </summary>
        public PayloadFilters Include { get; set; }

        /// <summary>
        /// Remove the features and auto experiments that match any filter.
        /// </summary>
        public PayloadFilters Exclude { get; set; }

        /// <summary>
        /// How prerequisites are treated. Defaults to <see cref="PrerequisiteFiltering.Keep"/>.
        /// </summary>
        public PrerequisiteFiltering Prerequisites { get; set; } = PrerequisiteFiltering.Keep;
    }

    /// <summary>
    /// Shrinks an SDK payload for a user whose attributes are only partly known, for example on a server that
    /// bootstraps a client-side SDK. The reduced payload evaluates the same as the full one for anyone who agrees
    /// with the known attributes.
    /// </summary>
    public static class PayloadReducer
    {
        private const string HoldoutPrefix = "$holdout:";

        private static readonly ConditionEvaluationProvider Evaluator =
            new ConditionEvaluationProvider(NullLogger<ConditionEvaluationProvider>.Instance);

        /// <summary>
        /// Partially evaluates a condition when only some attributes are known. Attributes missing from
        /// <paramref name="attributes"/> are unknown.
        /// </summary>
        /// <param name="attributes">The known attributes.</param>
        /// <param name="condition">The condition to prune.</param>
        /// <param name="savedGroups">The payload's saved groups, if any.</param>
        /// <returns>
        /// A boolean token once the result can't depend on the unknown attributes, otherwise the condition minus
        /// every branch that is already decided.
        /// </returns>
        public static JToken PruneCondition(JObject attributes, JObject condition, JObject savedGroups = null)
        {
            if (condition is null)
            {
                throw new ArgumentNullException(nameof(condition));
            }

            return Evaluator.PruneCondition(attributes, condition, savedGroups);
        }

        /// <summary>
        /// Reduces a decrypted SDK payload for a user whose attributes are only partly known.
        /// </summary>
        /// <param name="payload">
        /// The decrypted payload, with <c>features</c> and optionally <c>experiments</c>, <c>savedGroups</c> and
        /// <c>contextualBandits</c>. Other keys are copied as they are.
        /// </param>
        /// <param name="attributes">The known attributes. Anything missing is unknown.</param>
        /// <param name="options">Filters by project, tag or custom field, and how prerequisites are treated.</param>
        /// <returns>A new payload; the input is not modified.</returns>
        public static JObject ReducePayload(JObject payload, JObject attributes = null, ReducePayloadOptions options = null)
        {
            if (payload is null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return new Reduction(payload, attributes ?? new JObject(), options ?? new ReducePayloadOptions()).Run();
        }

        private sealed class Reduction
        {
            private readonly JObject _payload;
            private readonly JObject _attributes;
            private readonly PayloadFilters _include;
            private readonly PayloadFilters _exclude;
            private readonly PrerequisiteFiltering _prerequisites;
            private readonly JObject _savedGroups;
            private readonly JObject _allFeatures;

            // Insertion order is kept, so the output lists features in the order they were added
            private readonly Dictionary<string, JObject> _added = new Dictionary<string, JObject>();
            private readonly List<string> _addedOrder = new List<string>();

            public Reduction(JObject payload, JObject attributes, ReducePayloadOptions options)
            {
                _payload = payload;
                _attributes = attributes;
                _include = options.Include ?? new PayloadFilters();
                _exclude = options.Exclude ?? new PayloadFilters();
                _prerequisites = options.Prerequisites;
                _savedGroups = payload["savedGroups"] as JObject ?? new JObject();
                _allFeatures = payload["features"] as JObject ?? new JObject();
            }

            public JObject Run()
            {
                foreach (var property in _allFeatures.Properties().ToList())
                {
                    if (Matches(Metadata(property.Value)))
                    {
                        Add(property.Name);
                    }
                }

                var experiments = new JArray(
                    (_payload["experiments"] as JArray ?? new JArray())
                        .Where(x => Matches(Metadata(x)))
                        .Select(Prune));

                foreach (var experiment in experiments)
                {
                    AddParents(experiment);
                }

                var features = new JObject();

                foreach (var id in _addedOrder)
                {
                    features[id] = _added[id];
                }

                var contextualBandits = ReduceContextualBandits(features, experiments);

                var output = (JObject)_payload.DeepClone();
                output["features"] = features;
                output["experiments"] = experiments;
                output["savedGroups"] = KeepSavedGroups(features, experiments, contextualBandits);

                // Only when there were any, as with the payload's other optional keys
                if (_payload["contextualBandits"] is JObject)
                {
                    output["contextualBandits"] = contextualBandits;
                }

                return output;
            }

            private static JObject Metadata(JToken item) => (item as JObject)?["metadata"] as JObject ?? new JObject();

            // Only a list counts, as with $in, and any one of its values is enough
            private static bool Has(JToken actual, IEnumerable<JToken> wanted)
            {
                if (wanted is null)
                {
                    return false;
                }

                var wantedList = wanted.ToList();
                var actualValues = actual is JArray array ? array.ToList() : new List<JToken> { actual };

                return actualValues.Any(a => wantedList.Any(w => ScalarEquals(a, w)));
            }

            private static bool Has(JToken actual, IList<string> wanted) =>
                wanted != null && Has(actual, wanted.Select(x => (JToken)x));

            // Like JS ===: objects and arrays never equal, numbers compare by value
            private static bool ScalarEquals(JToken left, JToken right)
            {
                if (!(left is JValue l) || !(right is JValue r) || l.Type == JTokenType.Null || r.Type == JTokenType.Null)
                {
                    return false;
                }

                var leftIsNumber = l.Type == JTokenType.Integer || l.Type == JTokenType.Float;
                var rightIsNumber = r.Type == JTokenType.Integer || r.Type == JTokenType.Float;

                if (leftIsNumber && rightIsNumber)
                {
                    return Convert.ToDouble(l.Value) == Convert.ToDouble(r.Value);
                }

                return l.Type == r.Type && Equals(l.Value, r.Value);
            }

            // A rule without projects applies wherever its feature is delivered
            private bool RuleInProjects(JObject metadata) =>
                _include.Projects is null || !IsTruthy(metadata["projects"]) || Has(metadata["projects"], _include.Projects);

            // A feature or experiment has to list one of the projects, or be in all of them. One without projects
            // is in none, like one without tags.
            private bool InProjects(JObject metadata) =>
                _include.Projects is null || IsTruthy(metadata["allProjects"]) || Has(metadata["projects"], _include.Projects);

            // Kept when it matches every include filter and no exclude filter. Only listed projects are excluded,
            // so one in all projects stays.
            private bool Matches(JObject metadata)
            {
                var customFields = metadata["customFields"] as JObject ?? new JObject();

                return InProjects(metadata)
                    && (_include.Tags is null || Has(metadata["tags"], _include.Tags))
                    && (_include.CustomFields ?? new Dictionary<string, IList<JToken>>()).All(x => Has(customFields[x.Key], x.Value))
                    && !Has(metadata["projects"], _exclude.Projects)
                    && !Has(metadata["tags"], _exclude.Tags)
                    && !(_exclude.CustomFields ?? new Dictionary<string, IList<JToken>>()).Any(x => Has(customFields[x.Key], x.Value));
            }

            private static bool IsTruthy(JToken token)
            {
                if (token is null || token.Type == JTokenType.Null)
                {
                    return false;
                }

                switch (token.Type)
                {
                    case JTokenType.Boolean:
                        return token.Value<bool>();
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        var number = token.Value<double>();
                        return number != 0 && !double.IsNaN(number);
                    case JTokenType.String:
                        return token.ToString().Length > 0;
                    default:
                        return true;
                }
            }

            // Reduces a rule or an auto experiment
            private JObject Prune(JToken item)
            {
                var output = (JObject)item.DeepClone();

                if (!(output["condition"] is JObject condition))
                {
                    return output;
                }

                var pruned = Evaluator.PruneCondition(_attributes, condition, _savedGroups);

                if (pruned.Type == JTokenType.Boolean)
                {
                    if (pruned.Value<bool>())
                    {
                        output.Remove("condition");
                    }
                    else
                    {
                        // Kept, since sticky buckets and forced variations skip an experiment's condition, and a
                        // rule's prerequisites are checked before it
                        output["condition"] = new JObject { ["$not"] = new JObject() };

                        // A force rule that never passes never returns its value
                        if (output.ContainsKey("force"))
                        {
                            output["force"] = JValue.CreateNull();
                        }
                    }
                }
                else
                {
                    output["condition"] = pruned;
                }

                return output;
            }

            // Adds the prerequisites the client evaluates for an item, even when the filters drop them. With
            // Filter, only the filters decide, except for holdouts, which aren't app features and are always kept.
            private void AddParents(JToken item)
            {
                if (!(item["parentConditions"] is JArray parents))
                {
                    return;
                }

                foreach (var parent in parents)
                {
                    var id = parent["id"]?.Type == JTokenType.String ? parent["id"].ToString() : null;

                    if (id is null)
                    {
                        continue;
                    }

                    if (_prerequisites != PrerequisiteFiltering.Filter || id.StartsWith(HoldoutPrefix, StringComparison.Ordinal))
                    {
                        Add(id);
                    }
                }
            }

            private void Add(string id)
            {
                if (!(_allFeatures[id] is JObject feature) || _added.ContainsKey(id))
                {
                    return;
                }

                var output = (JObject)feature.DeepClone();

                if (feature["rules"] is JArray rules)
                {
                    // Rules scoped to other projects aren't served. A prerequisite from another project keeps its
                    // own, as on the server.
                    var scoped = InProjects(Metadata(feature));

                    output["rules"] = new JArray(rules
                        .Where(r => !scoped || RuleInProjects(Metadata(r)))
                        .Select(Prune));
                }

                // Stored before its parents are added, so a prerequisite cycle ends
                _added[id] = output;
                _addedOrder.Add(id);

                if (output["rules"] is JArray prunedRules)
                {
                    foreach (var rule in prunedRules)
                    {
                        AddParents(rule);
                    }
                }
            }

            // The contextual bandits that kept rules and experiments use. A context whose condition never passes
            // can't be the first that does, so it goes.
            private JObject ReduceContextualBandits(JObject features, JArray experiments)
            {
                var allBandits = _payload["contextualBandits"] as JObject ?? new JObject();
                var bandits = new JObject();

                var items = features.Properties()
                    .SelectMany(x => (x.Value["rules"] as JArray)?.Children() ?? Enumerable.Empty<JToken>())
                    .Concat(experiments);

                foreach (var item in items)
                {
                    var reference = item["contextualBanditRef"];

                    if (reference?.Type != JTokenType.String)
                    {
                        continue;
                    }

                    var id = reference.ToString();

                    if (bandits.ContainsKey(id) || !(allBandits[id] is JObject bandit))
                    {
                        continue;
                    }

                    var output = (JObject)bandit.DeepClone();
                    var contexts = new JArray();

                    foreach (var context in bandit["contexts"] as JArray ?? new JArray())
                    {
                        var condition = context["condition"] as JObject ?? new JObject();
                        var pruned = Evaluator.PruneCondition(_attributes, condition, _savedGroups);

                        if (pruned.Type == JTokenType.Boolean && !pruned.Value<bool>())
                        {
                            continue;
                        }

                        var prunedContext = (JObject)context.DeepClone();
                        prunedContext["condition"] = pruned.Type == JTokenType.Boolean ? new JObject() : pruned;
                        contexts.Add(prunedContext);
                    }

                    output["contexts"] = contexts;
                    bandits[id] = output;
                }

                return bandits;
            }

            // Keeps each saved group whose id appears as a string anywhere, with condition groups reduced. This
            // needs no list of the operators that reference groups.
            private JObject KeepSavedGroups(params JToken[] roots)
            {
                var groups = new JObject();
                var pending = new Stack<JToken>(roots.Reverse());

                while (pending.Count > 0)
                {
                    var token = pending.Pop();

                    if (token.Type == JTokenType.String)
                    {
                        var id = token.ToString();

                        if (!_savedGroups.ContainsKey(id) || groups.ContainsKey(id))
                        {
                            continue;
                        }

                        var group = _savedGroups[id].DeepClone();
                        var condition = Evaluator.PruneSavedGroup(_attributes, id, _savedGroups);

                        if (condition is JObject && group is JObject groupObj)
                        {
                            groupObj["condition"] = condition;
                        }

                        groups[id] = group;
                        pending.Push(group);
                    }
                    else if (token is JObject obj)
                    {
                        foreach (var value in obj.PropertyValues().Reverse())
                        {
                            pending.Push(value);
                        }
                    }
                    else if (token is JArray array)
                    {
                        foreach (var value in array.Reverse())
                        {
                            pending.Push(value);
                        }
                    }
                }

                return groups;
            }
        }
    }
}
