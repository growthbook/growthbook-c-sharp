using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GrowthBook.Tests.Extensions;

internal static class MathExtensions
{
    public static float Round(this float value) => float.Round(value, 6);
}
