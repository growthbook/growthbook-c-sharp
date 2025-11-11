using System.Text.Json.Nodes;
using GrowthBook.Converters;
using System.Text.Json.Serialization;
using System;

namespace GrowthBook
{
    /// <summary>
    /// A tuple that specifies what part of a namespace an experiment includes.
    /// If two experiments are in the same namespace and their ranges don't overlap, they wil be mutually exclusive.
    /// </summary>
    [JsonConverter(typeof(NamespaceConverter))]
    public class Namespace
    {
        public Namespace(string id, double start, double end)
        {
            Id = id;
            Start = start;
            End = end;
        }

        public Namespace(JsonArray jsonArray) :
            this(
                jsonArray[0]!.ToString(), jsonArray[1]!.GetValue<double>(), jsonArray[2]!.GetValue<double>()
                )
        { }

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

        public override bool Equals(object? obj)
        {
            if (obj is Namespace objNamespace)
            {
                return Id == objNamespace.Id && Start == objNamespace.Start && End == objNamespace.End;
            }
            return false;
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }
    }
}
