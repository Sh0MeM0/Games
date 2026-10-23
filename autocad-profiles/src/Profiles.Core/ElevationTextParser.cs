using System;
using System.Text.RegularExpressions;

namespace Profiles.Core
{
    /// <summary>Reads one unambiguous elevation from a plain TEXT/MTEXT/attribute caption.</summary>
    public static class ElevationTextParser
    {
        private static readonly Regex Number = new Regex(@"[+-]?(?:[0-9]+(?:[.,][0-9]+)?|(?<![\p{L}0-9])[.,][0-9]+)", RegexOptions.CultureInvariant);
        private static readonly Regex SeparatedSign = new Regex(@"[+-]\s+[0-9.,]", RegexOptions.CultureInvariant);

        public static double Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
                throw new ProfileValidationException("В выбранной подписи нет одной однозначной отметки.");
            string text = value.Replace('\u2212', '-').Trim();
            // Native MText.Text supplies plain text. Never interpret unevaluated
            // field expressions, stacked fractions or raw formatting as heights.
            if (text.Contains("%<") || text.Contains(">%") || text.Contains("\\") || text.Contains("±") || SeparatedSign.IsMatch(text))
                throw new ProfileValidationException("Подпись содержит поле или неоднозначное число. Введите отметку вручную.");
            var matches = Number.Matches(text);
            if (matches.Count != 1)
                throw new ProfileValidationException("Выберите подпись с одной отметкой: несколько чисел нельзя распознать однозначно.");
            var match = matches[0];
            // A decimal separator touching the matched token must belong to it;
            // otherwise malformed forms such as 160. or .160. could lose data.
            if ((match.Index > 0 && (text[match.Index - 1] == '.' || text[match.Index - 1] == ',') &&
                    !(match.Index > 1 && char.IsLetter(text[match.Index - 2]))) ||
                (match.Index + match.Length < text.Length && (text[match.Index + match.Length] == '.' || text[match.Index + match.Length] == ',')))
                throw new ProfileValidationException("Некорректная запись отметки. Введите число вручную.");
            double result = NumberParser.ParseRequired(match.Value, "Отметка");
            ProfileValidation.Elevation(result, "Отметка");
            return result;
        }
    }
}
