using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Services
{
    public interface IStickyBucketService
    {
        StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue);
        void SaveAssignments(StickyAssignmentsDocument document);
        IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IDictionary<string, string> attributes);
    }
}
