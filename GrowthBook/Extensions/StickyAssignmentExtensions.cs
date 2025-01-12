using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Extensions
{
    public static class StickyAssignmentExtensions
    {
        public static IDictionary<TKey, TValue> MergeWith<TKey, TValue>(this IDictionary<TKey, TValue> mergedData, IDictionary<TKey, TValue> additionalData)
        {
            foreach (var pair in additionalData)
            {
                mergedData[pair.Key] = pair.Value;
            }

            return mergedData;
        }

        public static IDictionary<TKey, TValue> MergeWith<TKey, TValue>(this IDictionary<TKey, TValue> mergedData, IEnumerable<IDictionary<TKey, TValue>> additionalData)
        {
            foreach(var dictionary in additionalData)
            {
                mergedData = mergedData.MergeWith(dictionary);
            }

            return mergedData;
        }
    }
}
