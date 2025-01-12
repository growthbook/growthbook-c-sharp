using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook
{
    public class StickyBucketVariation
    {
        public int VariationIndex { get; set; }
        public bool IsVersionBlocked { get; set; }

        public StickyBucketVariation(int variationIndex, bool isVersionBlocked)
        {
            VariationIndex = variationIndex;
            IsVersionBlocked = isVersionBlocked;
        }
    }
}
