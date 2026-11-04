using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GrowthBook.Tests.Extensions;

internal static class MathExtensions
{
    public static double Round(this double value, int? digitsOfPrecision = null) => Math.Round(value, digitsOfPrecision ?? 6);

    public static IEnumerable<double> RoundAll(this IEnumerable<double> values, int? digitsOfPrecision = null) => values.Select(x => x.Round(digitsOfPrecision));

    /// <summary>
    /// Floors to 8 decimal places, as the JS SDK's spec runner does before comparing computed values.
    /// </summary>
    public static double Floor8(this double value) => Math.Floor(value * 1e8) / 1e8;
}
