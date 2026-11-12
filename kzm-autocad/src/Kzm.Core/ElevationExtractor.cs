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
        OutOfRange,
        MultipleInRange,
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
                case ImportReason.OutOfRange: return "вне диапазона";
                case ImportReason.MultipleInRange: return "неск. чисел";
                case ImportReason.NoMarker: return "нет маркера";
                case ImportReason.MarkerTie: return "2 маркера";
                case ImportReason.MarkerShared: return "общий маркер";
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
                case ImportReason.NoNumber: return "нет числа по шаблону";
                case ImportReason.OutOfRange: return "вне диапазона отметок";
                case ImportReason.MultipleInRange: return "несколько чисел в диапазоне";
                case ImportReason.NoMarker: return "нет маркера в радиусе R";
                case ImportReason.MarkerTie: return "два маркера на одинаковом расстоянии";
                case ImportReason.MarkerShared: return "маркер ближайший к нескольким отметкам";
                case ImportReason.AlreadyImported: return "уже импортирован в эту поверхность";
                case ImportReason.ManualSkipped: return "исключено вручную";
                case ImportReason.ManualIncomplete: return "ручная правка не закончена";
                default: return string.Empty;
            }
        }
    }

    /// <summary>Elevation search rules taken from the drawing settings.</summary>
    public sealed class ElevationRules
    {
        public const string NumberGroup = "num";
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        public ElevationRules(string pattern, double min, double max, int decimals)
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new KzmException("Шаблон числа не задан.");
            if (!Guard.Finite(min) || !Guard.Finite(max)) throw new KzmException("Диапазон отметок не задан.");
            if (min >= max) throw new KzmException("Нижняя граница диапазона отметок должна быть меньше верхней.");
            Pattern = pattern;
            Regex = Compile(pattern);
            Min = min;
            Max = max;
            Decimals = decimals;
        }

        public string Pattern { get; }
        public Regex Regex { get; }
        public double Min { get; }
        public double Max { get; }
        public int Decimals { get; }

        public string RangeText => ElevationFormat.Format(Min, Decimals) + "…" + ElevationFormat.Format(Max, Decimals);

        public static Regex Compile(string pattern)
        {
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException exception)
            {
                throw new KzmException("Шаблон числа содержит ошибку: " + exception.Message, exception);
            }
        }
    }

    public sealed class ExtractResult
    {
        internal ExtractResult(ImportStatus status, ImportReason reason, double? value, IReadOnlyList<double> numbers,
            IReadOnlyList<double> inRange, string message)
        {
            Status = status;
            Reason = reason;
            Value = value;
            Numbers = numbers;
            InRange = inRange;
            Message = message;
        }

        public ImportStatus Status { get; }
        public ImportReason Reason { get; }
        public double? Value { get; }
        /// <summary>All numbers found by the pattern.</summary>
        public IReadOnlyList<double> Numbers { get; }
        /// <summary>Numbers inside [min…max].</summary>
        public IReadOnlyList<double> InRange { get; }
        public string Message { get; }
    }

    /// <summary>
    /// Finds the elevation in a plain text: all pattern matches → only numbers in
    /// [min…max] remain → one → accepted, none → rejected, several → disputed.
    /// </summary>
    public static class ElevationExtractor
    {
        public static ExtractResult Extract(string plain, ElevationRules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            string text = ElevationFormat.NormalizeSigns(plain ?? string.Empty);
            var empty = new double[0];
            if (text.Trim().Length == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.EmptyText, null, empty, empty, "пустой текст");

            var numbers = new List<double>();
            MatchCollection matches;
            try
            {
                matches = rules.Regex.Matches(text);
                foreach (Match match in matches)
                {
                    Group group = match.Groups[ElevationRules.NumberGroup];
                    string token = group.Success ? group.Value : match.Value;
                    token = token.Trim();
                    if (ElevationFormat.TryParseNormalized(token, out double number)) numbers.Add(number);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return new ExtractResult(ImportStatus.Rejected, ImportReason.NoNumber, null, empty, empty,
                    "шаблон числа обрабатывает текст слишком долго — упростите шаблон");
            }

            if (numbers.Count == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.NoNumber, null, empty, empty,
                    "нет числа по шаблону");

            var inRange = numbers.Where(value => value >= rules.Min && value <= rules.Max).ToList();
            if (inRange.Count == 0)
                return new ExtractResult(ImportStatus.Rejected, ImportReason.OutOfRange, null, numbers, inRange,
                    "вне диапазона " + rules.RangeText + ": " + Join(numbers, rules.Decimals));
            if (inRange.Count > 1)
                return new ExtractResult(ImportStatus.Disputed, ImportReason.MultipleInRange, null, numbers, inRange,
                    "несколько чисел в диапазоне " + rules.RangeText + ": " + Join(inRange, rules.Decimals));
            return new ExtractResult(ImportStatus.Accepted, ImportReason.None, inRange[0], numbers, inRange, string.Empty);
        }

        internal static string Join(IEnumerable<double> values, int decimals)
        {
            // Show the numbers as they were read (up to 4 decimals) so that «12» stays «12».
            return string.Join("; ", values.Select(value => Plain(value, Math.Max(decimals, 0))));
        }

        private static string Plain(double value, int decimals)
        {
            for (int digits = 0; digits <= 4; digits++)
            {
                double rounded = Math.Round(value, digits);
                if (Math.Abs(rounded - value) < 1e-9) return ElevationFormat.Format(value, digits);
            }
            return ElevationFormat.Format(value, Math.Max(decimals, 4));
        }
    }
}
