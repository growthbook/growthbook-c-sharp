using System.Collections.Generic;

namespace GrowthBook.Evaluation
{
    /// <summary>Tracks features evaluated in the current call stack to detect cyclic prerequisites.</summary>
    internal sealed class StackContext
    {
        public HashSet<string> EvaluatedFeatures { get; set; } = new HashSet<string>();
    }
}
