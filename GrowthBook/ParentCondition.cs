using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

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
        public string Id { get; set; }

        /// <summary>
        /// The condition to evaluate.
        /// </summary>
        public JObject Condition { get; set; }

        /// <summary>
        /// Requires that the parent feature must be set to on if true.
        /// </summary>
        public bool Gate { get; set; }

        /// <summary>
        /// A copy that shares nothing mutable with this one, for handing to a plugin.
        /// </summary>
        internal ParentCondition Copy()
        {
            var copy = (ParentCondition)MemberwiseClone();
            copy.Condition = (JObject)Condition?.DeepClone();

            return copy;
        }
    }
}
