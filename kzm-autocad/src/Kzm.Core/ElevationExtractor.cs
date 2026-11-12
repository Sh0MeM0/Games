using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kzm.Core
{
    public enum ImportStatus
    {
        Accepted = 0,
        Rejected = 1,
        Disputed = 2,
        Skipped = 3,
    }

    public enum ImportReason
    {
        None = 0,
        EmptyText,
        NoNumber,
        IntegerOnly,
        MultipleNumbers,
        NoMarker,
        MarkerTie,
        MarkerShared,
        AlreadyImported,
        ManualSkipped,
        ManualIncomplete,
    }

    public static class ImportReasons
    {
        /// <summary>Short label drawn next to a problem object on the check layer.</summary>
        public static string ShortLabel(ImportReason reason)
        {
            switch (reason)
            {
                case ImportReason.EmptyText: return "пусто";
                case ImportReason.NoNumber: return "нет числа";
                case ImportReason.IntegerOnly: return "целое число";
                case ImportReason.MultipleNumbers: return "неск. чисел";
                case ImportReason.NoMarker: return "нет маркера";
                case ImportReason.MarkerTie: return "2 маркера";
                case ImportReason.MarkerShared: return "чужой маркер";
                case ImportReason.AlreadyImported: return "уже есть";
                case ImportReason.ManualSkipped: return "исключено";
                case ImportReason.ManualIncomplete: return "дозаполнить";
                default: return string.Empty;
            }
        }

        public static string Title(ImportReason reason)
        {
            switch (reason)
            {
                case ImportReason.EmptyText: return "пустой текст";
                case ImportReason.NoNumber: return "нет числа";
                case ImportReason.IntegerOnly: return "только целое число — не отметка";
                case ImportReason.MultipleNumbers: return "несколько дробных чисел";
                case ImportReason.NoMarker: return "маркеры не найдены";
                case ImportReason.MarkerTie: return "два маркера на одинаковом расстоянии";
                case ImportReason.MarkerShared: return "ближайший маркер относится к другой отметке";
                case ImportReason.AlreadyImported: return "уже импортирован в эту поверхность";
                case ImportReason.ManualSkipped: return "исключено вручную";
                case ImportReason.ManualIncomplete: return "ручная правка не закончена";
                default: return string.Empty;
            }
        }
    }

    public sealed class ExtractResult
    {
        internal ExtractResult(ImportStatus status, ImportReason reason, double? value, IReadOnlyList<double> numbers,
            IReadOnlyList<double> decimals, string message, IReadOnlyList<string> integers = null)
        {
            Integers = integers ?? new string[0];
            Status = status;
            Reason = reason;
            Value = value;
            Numbers = numbers;
            Decimals = decimals;
            Message = message;
        }

        public ImportStatus Status { get; }
        public ImportReason Reason { get; }
        public double? Value { get; }
        /// <summary>All numbers of the text.</summary>
        public IReadOnlyList<double> Numbers { get; }
        /// <summary>Numbers with a fractional part — elevation candidates.</summary>
        public IReadOnlyList<double> Decimals { get; }
        /// <summary>Whole numbers as written (point numbers such as «15» in «т.15 145,32»).</summary>
        public IReadOnlyList<string> Integers { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Finds the elevation in a plain text. An elevation is a number with a fractional
    /// part (145,32 / 145.32 / −0,5). Whole numbers (point numbers such as «12»)
    /// are not taken. One decimal number → accepted; none → rejected with the reason;
    /// several → disputed.
    /// </summary>
    public static class ElevationExtractor
    {
        // A digit run with an optional fractional part; a separator must be followed by digits.
        private static readonly Regex Number = new Regex(@"(?<![0-9])[-+]?[0-9]+(?:[.,][0-9]+)?(?![0-9])",
            RegexOptions.CultureInvariant);

        public static ExtractResult Extract(string plain)
        {
            string text = ElevationFormat.NormalizeSigns(plain ?? string.Empty);
            var empty = new double[0];
            if (text.Trim().Length == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.EmptyText, null, empty, empty, "пустой текст");

            var numbers = new List<double>();
            var decimals = new List<double>();
            var integers = new List<string>();
            foreach (Match match in Number.Matches(text))
            {
                if (!ElevationFormat.TryParseNormalized(match.Value, out double value)) continue;
                numbers.Add(value);
                if (match.Value.IndexOf('.') >= 0 || match.Value.IndexOf(',') >= 0) decimals.Add(value);
                else if (match.Value[0] != '-' && match.Value[0] != '+') integers.Add(match.Value);
            }

            if (numbers.Count == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.NoNumber, null, numbers, decimals, "нет числа");
            if (decimals.Count == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.IntegerOnly, null, numbers, decimals,
                    "только целое число (номер точки?) — не отметка: " + Join(numbers), integers);
            if (decimals.Count > 1)
                return new ExtractResult(ImportStatus.Disputed, ImportReason.MultipleNumbers, null, numbers, decimals,
                    "несколько дробных чисел: " + Join(decimals), integers);
            return new ExtractResult(ImportStatus.Accepted, ImportReason.None, decimals[0], numbers, decimals, string.Empty, integers);
        }

        /// <summary>Numbers as they were read (up to 4 decimals), so «12» stays «12».</summary>
        internal static string Join(IEnumerable<double> values) => string.Join("; ", values.Select(Plain));

        private static string Plain(double value)
        {
            for (int digits = 0; digits <= 4; digits++)
                if (Math.Abs(Math.Round(value, digits) - value) < 1e-9) return ElevationFormat.Format(value, digits);
            return ElevationFormat.Format(value, 4);
        }
    }
}
