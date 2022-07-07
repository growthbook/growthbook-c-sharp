using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;

namespace GrowthBook {
    /// <summary>
    /// Represents a range of the numberline between 0 and 1.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class BucketRange {
        public BucketRange(double start, double end) {
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

        public override bool Equals(object obj) {
            if (obj.GetType() == typeof(BucketRange)) {
                BucketRange objRange = (BucketRange)obj;
                return this.Start == objRange.Start && this.End == objRange.End;
            }
            return false;
        }

        public override int GetHashCode() {
            throw new NotImplementedException();
        }
    }
}
