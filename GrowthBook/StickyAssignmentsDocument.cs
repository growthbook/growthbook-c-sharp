using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace GrowthBook
{
    public class StickyAssignmentsDocument : IEquatable<StickyAssignmentsDocument>
    {
        public bool HasValue => !string.IsNullOrEmpty(AttributeValue);

        [JsonIgnore]
        public string FormattedAttribute => $"{AttributeName}||{AttributeValue}";

        [JsonPropertyName("attributeName")]
        public string? AttributeName { get; set; }

        [JsonPropertyName("attributeValue")]
        public string? AttributeValue { get; set; }

        [JsonPropertyName("assignments")]
        public IDictionary<string, string?> Assignments { get; set; } = new Dictionary<string, string?>();

        public StickyAssignmentsDocument() { }

        public StickyAssignmentsDocument(string? attributeName, string? attributeValue, IDictionary<string, string?>? stickyAssignments = default)
        {
            AttributeName = attributeName;
            AttributeValue = attributeValue;
            Assignments = stickyAssignments ?? new Dictionary<string, string?>();
        }

        public bool Equals(StickyAssignmentsDocument? other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;

            return AttributeName == other.AttributeName &&
                   AttributeValue == other.AttributeValue &&
                   AssignmentsEqual(Assignments, other.Assignments);
        }

        private static bool AssignmentsEqual(IDictionary<string, string?>? first, IDictionary<string, string?>? second)
        {
            if (first == null && second == null) return true;
            if (first == null || second == null) return false;
            if (first.Count != second.Count) return false;

            foreach (var kvp in first)
            {
                if (!second.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as StickyAssignmentsDocument);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(AttributeName);
            hash.Add(AttributeValue);
            foreach (var kvp in Assignments ?? Enumerable.Empty<KeyValuePair<string, string?>>())
            {
                hash.Add(kvp.Key);
                hash.Add(kvp.Value);
            }
            return hash.ToHashCode();
        }

        public override string ToString()
        {
            var assignments = Assignments != null
                ? string.Join(", ", Assignments.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                : "none";
            return $"{FormattedAttribute} [{assignments}]";
        }
    }
}
