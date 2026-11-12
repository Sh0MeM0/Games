using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Settings stored in the drawing. Values the task does not fix are empty
    /// by default and are asked from the user; nothing normative is invented.
    /// </summary>
    public sealed class KzmSettings
    {
        /// <summary>Proposed in the stage 1 questions and accepted by the user.</summary>
        public const string DefaultPattern = @"[-+]?[0-9]+(?:[.,][0-9]+)?";
        /// <summary>Points closer than 0,01 m are reported (task, п. 2.1).</summary>
        public const double DefaultDuplicateTolerance = 0.01;
        /// <summary>Two decimals (task, п. 4; СТБ 2073-2010, п. 3.15).</summary>
        public const int DefaultDecimals = 2;
        public static readonly int[] Scales = { 500, 1000, 2000 };

        public string NumberPattern { get; set; } = DefaultPattern;
        public double? MinElevation { get; set; }
        public double? MaxElevation { get; set; }
        public double? MarkerRadius { get; set; }
        public double DuplicateTolerance { get; set; } = DefaultDuplicateTolerance;
        public int? ScaleDenominator { get; set; }
        public double? LabelHeightMm { get; set; }
        public double? PointSizeMm { get; set; }
        public int Decimals { get; set; } = DefaultDecimals;
        /// <summary>Last choices in KZM_IMPORT, offered as defaults next time.</summary>
        public XyMode LastXyMode { get; set; } = XyMode.Marker;
        public string LastSelectionMode { get; set; } = "WINDOW";

        public KzmSettings Clone() => (KzmSettings)MemberwiseClone();

        /// <summary>Drawing units per paper millimetre (1 unit = 1 m): 1:500 → 0,5.</summary>
        public double UnitsPerPaperMm
        {
            get
            {
                if (!ScaleDenominator.HasValue) throw new KzmException("Масштаб не задан.");
                return ScaleDenominator.Value / 1000.0;
            }
        }

        public ElevationRules CreateRules()
        {
            if (!MinElevation.HasValue || !MaxElevation.HasValue) throw new KzmException("Диапазон отметок не задан.");
            return new ElevationRules(NumberPattern, MinElevation.Value, MaxElevation.Value, Decimals);
        }

        /// <summary>Problems that prevent import; empty list means the settings are complete.</summary>
        public IReadOnlyList<string> ValidateForImport()
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(NumberPattern)) problems.Add("не задан шаблон числа");
            else
            {
                try { ElevationRules.Compile(NumberPattern); }
                catch (KzmException exception) { problems.Add(exception.Message); }
            }
            if (!MinElevation.HasValue || !MaxElevation.HasValue) problems.Add("не задан диапазон отметок [min…max]");
            else if (!(MinElevation.Value < MaxElevation.Value)) problems.Add("нижняя граница диапазона должна быть меньше верхней");
            if (!Positive(DuplicateTolerance)) problems.Add("допуск совпадения точек должен быть больше нуля");
            if (MarkerRadius.HasValue && !Positive(MarkerRadius.Value)) problems.Add("радиус поиска маркера должен быть больше нуля");
            problems.AddRange(ValidateDrawing());
            return problems;
        }

        /// <summary>Values needed to draw points and marks.</summary>
        public IReadOnlyList<string> ValidateDrawing()
        {
            var problems = new List<string>();
            if (!ScaleDenominator.HasValue) problems.Add("не задан масштаб (1:500 / 1:1000 / 1:2000)");
            else if (!Scales.Contains(ScaleDenominator.Value)) problems.Add("масштаб должен быть 1:500, 1:1000 или 1:2000");
            if (!LabelHeightMm.HasValue) problems.Add("не задана высота подписи отметки, мм");
            else if (!Positive(LabelHeightMm.Value)) problems.Add("высота подписи должна быть больше нуля");
            if (!PointSizeMm.HasValue) problems.Add("не задан размер знака точки, мм");
            else if (!Positive(PointSizeMm.Value)) problems.Add("размер знака точки должен быть больше нуля");
            if (Decimals < 0 || Decimals > 4) problems.Add("знаков после запятой: от 0 до 4");
            return problems;
        }

        private static bool Positive(double value) => Guard.Finite(value) && value > 0;
    }

    /// <summary>Key/value text form of the settings (stored in an Xrecord, invariant culture).</summary>
    public static class SettingsCodec
    {
        public const int Version = 1;

        public static IReadOnlyList<KeyValuePair<string, string>> Encode(KzmSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var values = new List<KeyValuePair<string, string>>
            {
                Pair("version", Version.ToString(CultureInfo.InvariantCulture)),
                Pair("pattern", settings.NumberPattern ?? string.Empty),
                Pair("elev_min", Number(settings.MinElevation)),
                Pair("elev_max", Number(settings.MaxElevation)),
                Pair("marker_radius", Number(settings.MarkerRadius)),
                Pair("dup_tol", Number(settings.DuplicateTolerance)),
                Pair("scale", settings.ScaleDenominator?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                Pair("label_height_mm", Number(settings.LabelHeightMm)),
                Pair("point_size_mm", Number(settings.PointSizeMm)),
                Pair("decimals", settings.Decimals.ToString(CultureInfo.InvariantCulture)),
                Pair("last_xy_mode", XyModes.Code(settings.LastXyMode)),
                Pair("last_selection", settings.LastSelectionMode ?? string.Empty),
            };
            return values;
        }

        public static KzmSettings Decode(IEnumerable<KeyValuePair<string, string>> values)
        {
            var settings = new KzmSettings();
            if (values == null) return settings;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values) map[pair.Key] = pair.Value ?? string.Empty;
            if (map.TryGetValue("version", out string version) && version != Version.ToString(CultureInfo.InvariantCulture))
                throw new KzmException("Настройки КЗМ в чертеже записаны более новой версией модуля (" + version + "). Обновите модуль.");
            if (map.TryGetValue("pattern", out string pattern) && pattern.Length > 0) settings.NumberPattern = pattern;
            settings.MinElevation = Optional(map, "elev_min");
            settings.MaxElevation = Optional(map, "elev_max");
            settings.MarkerRadius = Optional(map, "marker_radius");
            settings.DuplicateTolerance = Optional(map, "dup_tol") ?? KzmSettings.DefaultDuplicateTolerance;
            if (map.TryGetValue("scale", out string scale) && scale.Length > 0)
                settings.ScaleDenominator = int.Parse(scale, NumberStyles.Integer, CultureInfo.InvariantCulture);
            settings.LabelHeightMm = Optional(map, "label_height_mm");
            settings.PointSizeMm = Optional(map, "point_size_mm");
            if (map.TryGetValue("decimals", out string decimals) && decimals.Length > 0)
                settings.Decimals = int.Parse(decimals, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (map.TryGetValue("last_xy_mode", out string mode) && mode.Length > 0) settings.LastXyMode = XyModes.Parse(mode);
            if (map.TryGetValue("last_selection", out string selection) && selection.Length > 0) settings.LastSelectionMode = selection;
            return settings;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

        private static double? Optional(Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string text) || text.Length == 0) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !Guard.Finite(value))
                throw new KzmException("Настройка " + key + " в чертеже повреждена: «" + text + "».");
            return value;
        }
    }
}
