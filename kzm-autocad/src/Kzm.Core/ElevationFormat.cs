using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kzm.Core
{
    /// <summary>
    /// Numbers on the drawing: metres, fixed decimals, comma as the decimal
    /// separator (СТБ 2073-2010, п. 3.15: «с округлением до двух знаков после запятой»).
    /// </summary>
    public static class ElevationFormat
    {
        private static readonly Regex SingleNumber = new Regex(
            @"^[+\-]?(?:[0-9]+(?:[.,][0-9]*)?|[.,][0-9]+)$", RegexOptions.CultureInvariant);

        /// <summary>Rounds half away from zero in decimal arithmetic (145,325 → 145,33).</summary>
        public static string Format(double value, int decimals)
        {
            if (!Guard.Finite(value)) throw new KzmException("число не определено");
            if (decimals < 0 || decimals > 6) throw new ArgumentOutOfRangeException(nameof(decimals));
            string text;
            if (Math.Abs(value) < 7.9e27)
            {
                decimal rounded = Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
                text = rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            else
            {
                text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            if (text.StartsWith("-", StringComparison.Ordinal) && IsZero(text)) text = text.Substring(1);
            return text.Replace('.', ',');
        }

        /// <summary>Signed form used for working elevations and volumes: +0,50 / -0,25 / 0,00.</summary>
        public static string FormatSigned(double value, int decimals)
        {
            string text = Format(value, decimals);
            if (text.StartsWith("-", StringComparison.Ordinal) || IsZero(text)) return text;
            return "+" + text;
        }

        /// <summary>
        /// Число в ведомости объёмов, как в примере А.6 СТБ 2073-2010: в целой части из пяти и более цифр тысячи
        /// отделяются пробелом («150 000», «94 480»), четырёхзначные — без пробела («7320»); ноль — прочерк «—».
        /// </summary>
        public static string Grouped(double value, int decimals)
        {
            string text = Format(value, decimals);
            if (IsZero(text)) return "—";
            bool negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative) text = text.Substring(1);
            int comma = text.IndexOf(',');
            string whole = comma < 0 ? text : text.Substring(0, comma);
            string rest = comma < 0 ? string.Empty : text.Substring(comma);
            if (whole.Length >= 5)
            {
                var builder = new System.Text.StringBuilder();
                for (int i = 0; i < whole.Length; i++)
                {
                    if (i > 0 && (whole.Length - i) % 3 == 0) builder.Append(' ');
                    builder.Append(whole[i]);
                }
                whole = builder.ToString();
            }
            return (negative ? "-" : string.Empty) + whole + rest;
        }

        /// <summary>Exactly one number typed by the user: «145,32», «145.32», «−0,5».</summary>
        public static bool TryParse(string text, out double value)
        {
            value = 0;
            if (text == null) return false;
            string normalized = NormalizeSigns(text).Trim();
            if (normalized.Length == 0 || normalized.Length > 64 || !SingleNumber.IsMatch(normalized)) return false;
            return TryParseNormalized(normalized, out value);
        }

        internal static bool TryParseNormalized(string normalized, out double value)
        {
            string invariant = normalized.Replace(',', '.');
            if (invariant.EndsWith(".", StringComparison.Ordinal)) invariant = invariant.Substring(0, invariant.Length - 1);
            bool ok = double.TryParse(invariant, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value);
            return ok && Guard.Finite(value);
        }

        /// <summary>
        /// The Unicode minus sign (U+2212) is read as «-». Dashes (–, —) are not:
        /// they may separate ranges such as «140–160».
        /// </summary>
        public static string NormalizeSigns(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            return text.Replace('−', '-');
        }

        private static bool IsZero(string text)
        {
            foreach (char c in text)
                if (c >= '1' && c <= '9') return false;
            return true;
        }
    }
}
