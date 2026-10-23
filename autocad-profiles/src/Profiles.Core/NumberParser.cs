using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Profiles.Core
{
    public static class NumberParser
    {
        private static readonly Regex Decimal = new Regex(@"^[+-]?(?:[0-9]+(?:[.,][0-9]+)?|[.,][0-9]+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Picket = new Regex(@"^(?:ПК\s*)?([0-9]+)\s*\+\s*([0-9]+(?:[.,][0-9]+)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static double ParseRequired(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ProfileValidationException(field + ": введите число; пустая ячейка не равна нулю.");
            string text = value.Trim();
            Match station = Picket.Match(text);
            if (station.Success)
            {
                double whole = Read(station.Groups[1].Value, field);
                double remainder = Read(station.Groups[2].Value, field);
                if (remainder >= 100) throw new ProfileValidationException(field + ": остаток пикетажа должен быть меньше 100 м (пример 1+25.50).");
                double result = whole * 100 + remainder;
                if (!Finite(result)) throw Error(field);
                return result;
            }
            return Read(text, field);
        }

        public static double? ParseOptional(string value, string field)
        {
            return string.IsNullOrWhiteSpace(value) ? (double?)null : ParseRequired(value, field);
        }

        private static double Read(string value, string field)
        {
            double number;
            if (!Decimal.IsMatch(value) || !double.TryParse(value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) || !Finite(number))
                throw Error(field);
            return number;
        }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static ProfileValidationException Error(string field)
        {
            return new ProfileValidationException(field + ": укажите конечное число с точкой или запятой без разделителей тысяч (например 125.50); пикетаж — 1+25.50.");
        }
    }
}
