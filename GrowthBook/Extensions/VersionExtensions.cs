using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GrowthBook.Providers;

namespace GrowthBook.Extensions
{
    internal static class VersionExtensions
    {
        public static string ToPaddedVersionString(this string input) => ConditionEvaluationProvider.PaddedVersionString(input);
    }
}
