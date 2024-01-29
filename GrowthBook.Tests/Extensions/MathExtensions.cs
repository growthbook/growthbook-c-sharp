using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GrowthBook.Tests.Extensions;

internal static class MathExtensions
{
    public static double Round(this double value, int? digitsOfPrecision = null) => double.Round(value, digitsOfPrecision ?? 6);

    public static IEnumerable<double> RoundAll(this IEnumerable<double> values, int? digitsOfPrecision = null) => values.Select(x => x.Round(digitsOfPrecision));
}
