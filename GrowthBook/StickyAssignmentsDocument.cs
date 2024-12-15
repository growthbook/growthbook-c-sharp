using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    public class StickyAssignmentsDocument
    {
        public bool HasValue => AttributeValue != null;

        public string FormattedAttribute => $"{AttributeName}||{AttributeValue}";

        public string AttributeName { get; set; }
        public string AttributeValue { get; set; }
        public IDictionary<string, string> StickyAssignments { get; set; }

        public StickyAssignmentsDocument() { }

        public StickyAssignmentsDocument(string attributeName, string attributeValue, IDictionary<string, string> stickyAssignments = default)
        {
            AttributeName = attributeName;
            AttributeValue = attributeValue;
            StickyAssignments = stickyAssignments ?? new Dictionary<string, string>();
        }
    }
}
