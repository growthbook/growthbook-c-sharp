using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Services
{
    public interface IStickyBucketService
    {
        StickyAssignmentsDocument GetAssignments(string attributeName, string attributeValue);

        /// <summary>
        /// Replaces the whole document stored under the same attribute and value. The SDK serializes saves within
        /// one process only, so a store shared by several servers should merge assignments rather than overwrite them.
        /// </summary>
        void SaveAssignments(StickyAssignmentsDocument document);
        IDictionary<string, StickyAssignmentsDocument> GetAllAssignments(IEnumerable<string> attributes);
    }
}
