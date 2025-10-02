using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GrowthBook.Services
{
    public class InMemoryStickyBucketService : IStickyBucketService
    {
        private readonly IDictionary<string, StickyAssignmentsDocument> _cachedDocuments = new Dictionary<string, StickyAssignmentsDocument>();

        public IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes)
        {
            var assignments = from name in attributes
                              let existingDoc = _cachedDocuments.TryGetValue(name, out var doc) ? doc : null
                              where existingDoc != null
                              select (Attribute: existingDoc.FormattedAttribute, Document: existingDoc);

            return assignments.ToDictionary(x => x.Attribute, x => x.Document);
        }

        public StickyAssignmentsDocument? GetAssignments(string attributeName, string attributeValue)
        {
            var attribute = FormatAttribute(attributeName, attributeValue);

            return _cachedDocuments.TryGetValue(attribute, out var document) ? document : null;
        }

        public void SaveAssignments(StickyAssignmentsDocument document) => _cachedDocuments[document.FormattedAttribute] = document;

        private static string FormatAttribute(string attributeName, string attributeValue) => $"{attributeName}||{attributeValue}";
    }
}
