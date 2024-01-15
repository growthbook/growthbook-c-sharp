using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Providers
{
    public interface IConditionEvaluationProvider
    {
        bool EvalCondition(JToken attributes, JObject condition);
    }
}
