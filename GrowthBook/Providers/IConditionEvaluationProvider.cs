using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Providers
{
    /// <summary>
    /// Represents a provider that can evaluate GrowthBook JSON conditions.
    /// </summary>
    public interface IConditionEvaluationProvider
    {
        /// <summary>
        /// The main function used to evaluate a condition.
        /// </summary>
        /// <param name="attributes">The attributes to compare against.</param>
        /// <param name="condition">The condition to evaluate.</param>
        /// <returns>True if the attributes satisfy the condition.</returns>
        bool EvalCondition(JToken attributes, JObject condition);
    }
}
