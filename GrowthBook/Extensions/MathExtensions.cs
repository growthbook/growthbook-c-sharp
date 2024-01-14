using System;
using System.Collections.Generic;
using System.Text;

namespace GrowthBook.Extensions
{
    internal static class MathExtensions
    {
        public static float Round(this float value) => (float)Math.Round(value, 6);
    }
}
