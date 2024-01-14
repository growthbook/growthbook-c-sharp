using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents a range of the numberline between 0 and 1.
    /// </summary>
    [JsonConverter(typeof(BucketRangeTupleConverter))]
    public class BucketRange
    {
        public BucketRange(float start, float end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// The start of the range.
        /// </summary>
        public float Start { get; set; }

        /// <summary>
        /// The end of the range.
        /// </summary>
        public float End { get; set; }

        public override bool Equals(object obj)
        {
            if (obj.GetType() == typeof(BucketRange))
            {
                BucketRange objRange = (BucketRange)obj;
                return Start == objRange.Start && End == objRange.End;
            }
            return false;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
