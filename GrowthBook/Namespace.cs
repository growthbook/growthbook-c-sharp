using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace GrowthBook {
    /// <summary>
    /// A tuple that specifies what part of a namespace an experiment includes.
    /// If two experiments are in the same namespace and their ranges don't overlap, they wil be mutually exclusive.
    /// </summary>
    [JsonConverter(typeof(NamespaceTupleConverter))]
    public class Namespace {
        public Namespace(string id, double start, double end) {
            Id = id;
            Start = start;
            End = end;
        }

        public Namespace(JArray jArray) :
            this(jArray[0].ToString(), jArray[1].ToObject<double>(), jArray[2].ToObject<double>()) { }

        /// <summary>
        /// The namespace id.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// The beginning of the range (between 0 and 1).
        /// </summary>
        public double Start { get; }

        /// <summary>
        /// The end of the range (between 0 and 1).
        /// </summary>
        public double End { get; }

        public override bool Equals(object obj) {
            if (obj.GetType() == typeof(Namespace)) {
                Namespace objNamspace = (Namespace)obj;
                return Id == objNamspace.Id && Start == objNamspace.Start && End == objNamspace.End;
            }
            return false;
        }

        public override int GetHashCode() {
            throw new NotImplementedException();
        }
    }
}
