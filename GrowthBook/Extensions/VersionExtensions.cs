using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GrowthBook.Providers;

namespace GrowthBook.Extensions
{
    internal static class VersionExtensions
    {
        /// <summary>
        /// Convert a version string to the GrowthBook padded version string format for comparison purposes.
        /// </summary>
        /// <param name="input">The version string to convert.</param>
        /// <returns>The string in padded version format.</returns>
        public static string ToPaddedVersionString(this string input) => ConditionEvaluationProvider.PaddedVersionString(input);
    }
}
