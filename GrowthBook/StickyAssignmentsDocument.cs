using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    public class StickyAssignmentsDocument
    {
        public bool HasValue => AttributeValue != null;

        public string FormattedAttribute => $"{AttributeName}||{AttributeValue}";

        public string? AttributeName { get; set; }
        public string? AttributeValue { get; set; }
        public IDictionary<string, string> Assignments { get; set; } = new Dictionary<string, string>();

        public StickyAssignmentsDocument() { }

        public StickyAssignmentsDocument(string? attributeName, string? attributeValue, IDictionary<string, string>? stickyAssignments = default)
        {
            AttributeName = attributeName;
            AttributeValue = attributeValue;
            Assignments = stickyAssignments ?? new Dictionary<string, string>();
        }
    }
}
