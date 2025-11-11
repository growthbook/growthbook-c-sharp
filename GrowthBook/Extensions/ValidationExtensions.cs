using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Extensions
{
    internal static class ValidationExtensions
    {
        /// <summary>
        /// Determines whether the string is null, empty, or whitespace.
        /// </summary>
        /// <remarks>
        /// This is a convenience method to avoid making the code harder to read due to
        /// the larger amount of these checks that will happen at various points in the codebase.
        /// </remarks>
        /// <param name="value">The string to verify.</param>
        /// <returns>True if null, empty, or whitespace, false otherwise.</returns>
        public static bool IsNullOrWhitespace(this string? value) => string.IsNullOrWhiteSpace(value);
    }
}
