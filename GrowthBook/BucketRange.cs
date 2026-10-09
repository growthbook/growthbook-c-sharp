using System;
using GrowthBook.Converters;
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
        public BucketRange(double start, double end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// The start of the range.
        /// </summary>
        public double Start { get; set; }

        /// <summary>
        /// The end of the range.
        /// </summary>
        public double End { get; set; }

        public override bool Equals(object obj)
        {
            if (obj?.GetType() == typeof(BucketRange))
            {
                BucketRange objRange = (BucketRange)obj;
                return Start == objRange.Start && End == objRange.End;
            }
            return false;
        }

        /// <summary>
        /// A hash over exactly the fields Equals compares, so equal instances hash equally.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;

                hash = hash * 31 + Start.GetHashCode();
                hash = hash * 31 + End.GetHashCode();

                return hash;
            }
        }
    }
}
