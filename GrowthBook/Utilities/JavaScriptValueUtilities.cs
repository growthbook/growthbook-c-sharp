using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrowthBook.Utilities
{
    /// <summary>
    /// Mirrors the JavaScript value coercions the reference SDK relies on when evaluating conditions.
    /// A missing attribute (a C# null) is treated as a JavaScript null, which is what the reference's path lookup returns.
    /// </summary>
    internal static class JavaScriptValueUtilities
    {
        private static readonly Regex DecimalNumberPattern = new Regex(@"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Converts a value the way JavaScript's <c>value + ""</c> does.
        /// </summary>
        public static string ToJsString(JToken value)
        {
            if (value is null)
            {
                return "null";
            }

            switch (value.Type)
            {
                case JTokenType.Null:
                    return "null";
                case JTokenType.Undefined:
                    return "undefined";
                case JTokenType.Boolean:
                    return value.Value<bool>() ? "true" : "false";
                case JTokenType.Integer:
                    return Convert.ToString(((JValue)value).Value, CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return NumberToJsString(value.Value<double>());
                case JTokenType.Array:
                    return string.Join(",", value.Select(x => x.Type == JTokenType.Null || x.Type == JTokenType.Undefined ? string.Empty : ToJsString(x)));
                case JTokenType.Object:
                    return "[object Object]";
                default:
                    return value.ToString();
            }
        }

        /// <summary>
        /// Coerces a value the way JavaScript's <c>value * 1</c> does.
        /// </summary>
        public static double ToJsNumber(JToken value)
        {
            if (value is null)
            {
                return 0;
            }

            switch (value.Type)
            {
                case JTokenType.Null:
                    return 0;
                case JTokenType.Boolean:
                    return value.Value<bool>() ? 1 : 0;
                case JTokenType.Integer:
                case JTokenType.Float:
                    return value.Value<double>();
                case JTokenType.String:
                case JTokenType.Array:
                    return StringToJsNumber(ToJsString(value));
                default:
                    return double.NaN;
            }
        }

        /// <summary>
        /// Determines whether a value is truthy in JavaScript.
        /// </summary>
        public static bool IsJsTruthy(JToken value)
        {
            if (value is null)
            {
                return false;
            }

            switch (value.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return false;
                case JTokenType.Boolean:
                    return value.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    var number = value.Value<double>();
                    return number != 0 && !double.IsNaN(number);
                case JTokenType.String:
                    return value.Value<string>().Length > 0;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Serializes a value the way JavaScript's <c>JSON.stringify</c> does, keeping key order and writing numbers as JavaScript would.
        /// </summary>
        public static string ToJsJson(JToken value)
        {
            var builder = new StringBuilder();
            WriteJsJson(value, builder);
            return builder.ToString();
        }

        private static void WriteJsJson(JToken value, StringBuilder builder)
        {
            if (value is null)
            {
                builder.Append("null");
                return;
            }

            switch (value.Type)
            {
                case JTokenType.Array:
                    builder.Append('[');
                    var first = true;
                    foreach (var item in value)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }
                        first = false;
                        WriteJsJson(item, builder);
                    }
                    builder.Append(']');
                    return;
                case JTokenType.Object:
                    builder.Append('{');
                    var firstProperty = true;
                    foreach (var property in ((JObject)value).Properties())
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }
                        firstProperty = false;
                        builder.Append(JsonConvert.ToString(property.Name)).Append(':');
                        WriteJsJson(property.Value, builder);
                    }
                    builder.Append('}');
                    return;
                case JTokenType.Integer:
                case JTokenType.Float:
                    var number = value.Value<double>();
                    builder.Append(double.IsNaN(number) || double.IsInfinity(number) ? "null" : ToJsString(value));
                    return;
                case JTokenType.String:
                    builder.Append(JsonConvert.ToString(value.Value<string>()));
                    return;
                case JTokenType.Boolean:
                    builder.Append(value.Value<bool>() ? "true" : "false");
                    return;
                default:
                    builder.Append("null");
                    return;
            }
        }

        private static double StringToJsNumber(string input)
        {
            var trimmed = input.Trim();

            if (trimmed.Length == 0)
            {
                return 0;
            }

            if (trimmed.Length > 2 && trimmed[0] == '0')
            {
                var radix = char.ToLowerInvariant(trimmed[1]) == 'x' ? 16 : char.ToLowerInvariant(trimmed[1]) == 'o' ? 8 : char.ToLowerInvariant(trimmed[1]) == 'b' ? 2 : 0;

                if (radix != 0)
                {
                    try
                    {
                        return Convert.ToUInt64(trimmed.Substring(2), radix);
                    }
                    catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
                    {
                        return double.NaN;
                    }
                }
            }

            switch (trimmed)
            {
                case "Infinity":
                case "+Infinity":
                    return double.PositiveInfinity;
                case "-Infinity":
                    return double.NegativeInfinity;
            }

            if (!DecimalNumberPattern.IsMatch(trimmed))
            {
                return double.NaN;
            }

            return double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a double the way JavaScript's <c>Number.prototype.toString</c> does.
        /// </summary>
        private static string NumberToJsString(double value)
        {
            if (double.IsNaN(value))
            {
                return "NaN";
            }
            if (double.IsPositiveInfinity(value))
            {
                return "Infinity";
            }
            if (double.IsNegativeInfinity(value))
            {
                return "-Infinity";
            }
            if (value == 0)
            {
                return "0";
            }
            if (value < 0)
            {
                return "-" + NumberToJsString(-value);
            }

            // Shortest round-trip digits and the decimal exponent n, so that value = 0.digits * 10^n.
            var roundTrip = value.ToString("R", CultureInfo.InvariantCulture);
            var exponentIndex = roundTrip.IndexOf('E');
            var mantissa = exponentIndex >= 0 ? roundTrip.Substring(0, exponentIndex) : roundTrip;
            var exponent = exponentIndex >= 0 ? int.Parse(roundTrip.Substring(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;

            var dotIndex = mantissa.IndexOf('.');
            var digits = mantissa.Replace(".", string.Empty);
            var n = (dotIndex >= 0 ? dotIndex : mantissa.Length) + exponent;

            var leadingZeros = digits.Length - digits.TrimStart('0').Length;
            digits = digits.Substring(leadingZeros).TrimEnd('0');
            n -= leadingZeros;

            var k = digits.Length;

            if (k <= n && n <= 21)
            {
                return digits + new string('0', n - k);
            }
            if (0 < n && n <= 21)
            {
                return digits.Substring(0, n) + "." + digits.Substring(n);
            }
            if (-6 < n && n <= 0)
            {
                return "0." + new string('0', -n) + digits;
            }

            var e = n - 1;
            var exponentText = (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);

            return k == 1 ? digits + "e" + exponentText : digits.Substring(0, 1) + "." + digits.Substring(1) + "e" + exponentText;
        }
    }
}
