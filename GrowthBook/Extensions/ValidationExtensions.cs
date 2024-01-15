using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Extensions
{
    internal static class ValidationExtensions
    {
        public static bool IsMissing(this string value) => string.IsNullOrWhiteSpace(value);
    }
}
