using System;
using GrowthBook.Converters;
using System.Text.Json.Serialization;

namespace GrowthBook
{
    /// <summary>
    /// Represents a range of the numberline between 0 and 1.
    /// </summary>
    [JsonConverter(typeof(BucketRangeConverter))]
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

        public override bool Equals(object? obj)
        {
            if (obj is BucketRange objRange)
            {
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
