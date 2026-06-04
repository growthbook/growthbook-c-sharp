using System.Text.Json.Nodes;

namespace GrowthBook
{
    /// <summary>
    /// Represents a prerequisite for a condition.
    /// </summary>
    public class ParentCondition
    {
        /// <summary>
        /// The feature ID.
        /// </summary>
        public required string Id { get; set; }

        /// <summary>
        /// The condition to evaluate.
        /// </summary>
        public JsonObject? Condition { get; set; }

        /// <summary>
        /// Requires that the parent feature must be set to on if true.
        /// </summary>
        public bool Gate { get; set; }
    }
}
