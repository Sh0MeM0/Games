using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Settings of the drawing. Point appearance lives in each surface (taken from a
    /// sample text automatically); cartogram appearance lives in each cartogram.
    /// </summary>
    public sealed class KzmSettings
    {
        /// <summary>Points closer than 0,01 m are reported (task, п. 2.1). Fixed, not shown as a setting.</summary>
        public const double DuplicateTolerance = 0.01;
        /// <summary>Two decimals (task, п. 4; СТБ 2073-2010, п. 3.15).</summary>
        public const int DefaultDecimals = 2;
        public static readonly int[] Scales = { 500, 1000, 2000 };
        /// <summary>СТБ 2073-2010, п. 3.13: plans at 1:500 or 1:1000; 1:500 is offered first.</summary>
        public const int DefaultScale = 500;

        public int Decimals { get; set; } = DefaultDecimals;
        /// <summary>Scale proposed for new cartograms.</summary>
        public int Scale { get; set; } = DefaultScale;
        /// <summary>Last choices in the import form, offered as defaults next time.</summary>
        public XyMode LastXyMode { get; set; } = XyMode.Marker;
        public string LastSelectionMode { get; set; } = "WINDOW";

        public KzmSettings Clone() => (KzmSettings)MemberwiseClone();

        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (Decimals < 0 || Decimals > 4) problems.Add("знаков после запятой: от 0 до 4");
            if (!Scales.Contains(Scale)) problems.Add("масштаб должен быть 1:500, 1:1000 или 1:2000");
            return problems;
        }
    }

    /// <summary>Key/value text form of the settings (stored in an Xrecord, invariant culture).</summary>
    public static class SettingsCodec
    {
        public const int Version = 1;

        public static IReadOnlyList<KeyValuePair<string, string>> Encode(KzmSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new List<KeyValuePair<string, string>>
            {
                Pair("version", Version.ToString(CultureInfo.InvariantCulture)),
                Pair("decimals", settings.Decimals.ToString(CultureInfo.InvariantCulture)),
                Pair("scale", settings.Scale.ToString(CultureInfo.InvariantCulture)),
                Pair("last_xy_mode", XyModes.Code(settings.LastXyMode)),
                Pair("last_selection", settings.LastSelectionMode ?? string.Empty),
            };
        }

        /// <summary>Keys of versions 0.1–0.3 (pattern, range, R, tolerance, sizes in mm) are ignored.</summary>
        public static KzmSettings Decode(IEnumerable<KeyValuePair<string, string>> values)
        {
            var settings = new KzmSettings();
            if (values == null) return settings;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values) map[pair.Key] = pair.Value ?? string.Empty;
            if (map.TryGetValue("version", out string version) && version != Version.ToString(CultureInfo.InvariantCulture))
                throw new KzmException("Настройки КЗМ в чертеже записаны более новой версией модуля (" + version + "). Обновите модуль.");
            if (map.TryGetValue("decimals", out string decimals) && int.TryParse(decimals, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
                settings.Decimals = d;
            if (map.TryGetValue("scale", out string scale) && int.TryParse(scale, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) &&
                KzmSettings.Scales.Contains(s))
                settings.Scale = s;
            if (map.TryGetValue("last_xy_mode", out string mode) && mode.Length > 0) settings.LastXyMode = XyModes.Parse(mode);
            if (map.TryGetValue("last_selection", out string selection) && selection.Length > 0) settings.LastSelectionMode = selection;
            return settings;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

        internal static double? Optional(Dictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string text) || text.Length == 0) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !Guard.Finite(value))
                throw new KzmException("Настройка " + key + " в чертеже повреждена: «" + text + "».");
            return value;
        }
    }
}
