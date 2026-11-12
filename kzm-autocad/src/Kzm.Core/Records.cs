using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Оформление точек поверхности: стиль и высота текста берутся с образца (текста съёмки),
    /// цвета отметки и номера назначает пользователь. Высоты — в единицах чертежа (м).
    /// </summary>
    public sealed class PointAppearance
    {
        public const string ByLayer = "BYLAYER";

        public string TextStyle { get; set; }
        public double? TextHeight { get; set; }
        public double? NumberHeight { get; set; }
        /// <summary>«BYLAYER», «ACI:n» or «RGB:r,g,b».</summary>
        public string ElevationColor { get; set; } = ByLayer;
        public string NumberColor { get; set; } = ByLayer;
        /// <summary>Diameter of the point sign; empty — half of the text height.</summary>
        public double? SignSize { get; set; }
        public bool ShowNumber { get; set; } = true;

        public bool IsSet => TextHeight.HasValue && TextHeight.Value > 0;
        public double EffectiveNumberHeight => NumberHeight ?? TextHeight ?? 1;
        public double EffectiveSignSize => SignSize ?? (TextHeight ?? 1) * 0.5;

        public PointAppearance Clone() => (PointAppearance)MemberwiseClone();

        internal IEnumerable<KeyValuePair<string, string>> Encode()
        {
            yield return Pair("app_style", TextStyle ?? string.Empty);
            yield return Pair("app_h", Codec.Number(TextHeight));
            yield return Pair("app_nh", Codec.Number(NumberHeight));
            yield return Pair("app_ecol", ElevationColor ?? ByLayer);
            yield return Pair("app_ncol", NumberColor ?? ByLayer);
            yield return Pair("app_sign", Codec.Number(SignSize));
            yield return Pair("app_num", ShowNumber ? "1" : "0");
        }

        internal static PointAppearance Decode(IReadOnlyDictionary<string, string> map)
        {
            var appearance = new PointAppearance();
            if (map.TryGetValue("app_style", out string style) && style.Length > 0) appearance.TextStyle = style;
            appearance.TextHeight = Codec.Optional(map, "app_h");
            appearance.NumberHeight = Codec.Optional(map, "app_nh");
            if (map.TryGetValue("app_ecol", out string ecol) && ecol.Length > 0) appearance.ElevationColor = ecol;
            if (map.TryGetValue("app_ncol", out string ncol) && ncol.Length > 0) appearance.NumberColor = ncol;
            appearance.SignSize = Codec.Optional(map, "app_sign");
            if (map.TryGetValue("app_num", out string num)) appearance.ShowNumber = num != "0";
            return appearance;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    public static class SurfaceRecord
    {
        public static IReadOnlyList<KeyValuePair<string, string>> Encode(SurfaceInfo surface)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                Pair("name", surface.Name),
                Pair("kind", SurfaceKinds.Code(surface.Kind)),
                Pair("layer", surface.Layer),
                Pair("created", surface.Created ?? string.Empty),
                Pair("revision", surface.Revision.ToString(CultureInfo.InvariantCulture)),
                Pair("boundary", surface.BoundaryHandle ?? string.Empty),
                Pair("tin_shown", surface.ShowTriangles ? "1" : "0"),
                Pair("tin_fp", surface.TrianglesFingerprint ?? string.Empty),
            };
            values.AddRange((surface.Appearance ?? new PointAppearance()).Encode());
            return values;
        }

        public static SurfaceInfo Decode(string id, IReadOnlyDictionary<string, string> map)
        {
            if (!map.TryGetValue("name", out string name) || !map.TryGetValue("kind", out string kind) || !map.TryGetValue("layer", out string layer))
                throw new KzmException("в записи поверхности " + id + " нет имени, типа или слоя");
            return new SurfaceInfo
            {
                Id = id,
                Name = name,
                Kind = SurfaceKinds.Parse(kind),
                Layer = layer,
                Created = map.TryGetValue("created", out string created) ? created : string.Empty,
                Revision = map.TryGetValue("revision", out string revision) && int.TryParse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : 0,
                BoundaryHandle = map.TryGetValue("boundary", out string boundary) && boundary.Length > 0 ? boundary : null,
                ShowTriangles = map.TryGetValue("tin_shown", out string shown) && shown == "1",
                TrianglesFingerprint = map.TryGetValue("tin_fp", out string fp) && fp.Length > 0 ? fp : null,
                Appearance = PointAppearance.Decode(map),
            };
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    /// <summary>Картограмма: по каким поверхностям, границе и сетке построена, и её оформление.</summary>
    public sealed class CartogramInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FactSurfaceId { get; set; }
        public string ProjectSurfaceId { get; set; }
        public string BoundaryHandle { get; set; }
        /// <summary>Сторона квадрата, м (задание п. 4: 20 м; СТБ 2073-2010, п. 7.2).</summary>
        public double Step { get; set; } = 20;
        /// <summary>Базовая точка (узел 0,0); пусто — угол границы, округлённый до шага.</summary>
        public double? OriginX { get; set; }
        public double? OriginY { get; set; }
        /// <summary>Угол поворота сетки, градусы.</summary>
        public double AngleDegrees { get; set; }
        public LabelScheme Columns { get; set; } = LabelScheme.Numbers;
        public LabelScheme Rows { get; set; } = LabelScheme.Letters;
        public int Scale { get; set; } = KzmSettings.DefaultScale;
        /// <summary>Высоты текста, мм на листе (ряд размеров шрифта ГОСТ 2.304: 1,8; 2,5; 3,5 …).</summary>
        public double NodeTextMm { get; set; } = 1.8;
        public double VolumeTextMm { get; set; } = 2.5;
        public double TotalsTextMm { get; set; } = 2.5;
        /// <summary>Округление объёмов: знаков после запятой (задание п. 5: до 1 м³).</summary>
        public int VolumeDecimals { get; set; }
        public bool Built { get; set; }
        public string BuiltFingerprint { get; set; }
        public double LastFill { get; set; }
        public double LastCut { get; set; }
        public string BuiltAt { get; set; }

        public double UnitsPerMm => Scale / 1000.0;

        public CartogramInfo Clone() => (CartogramInfo)MemberwiseClone();

        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(Name)) problems.Add("не задано имя картограммы");
            if (string.IsNullOrEmpty(FactSurfaceId)) problems.Add("не выбрана фактическая поверхность");
            if (string.IsNullOrEmpty(ProjectSurfaceId)) problems.Add("не выбрана проектная поверхность");
            if (string.IsNullOrEmpty(BoundaryHandle)) problems.Add("не указана граница работ (замкнутая полилиния)");
            if (!(Step > 0)) problems.Add("шаг сетки должен быть больше нуля");
            if (!KzmSettings.Scales.Contains(Scale)) problems.Add("масштаб должен быть 1:500, 1:1000 или 1:2000");
            if (!(NodeTextMm > 0) || !(VolumeTextMm > 0) || !(TotalsTextMm > 0)) problems.Add("высоты текста должны быть больше нуля");
            if (VolumeDecimals < 0 || VolumeDecimals > 3) problems.Add("округление объёмов: от 0 до 3 знаков");
            return problems;
        }

        public static IReadOnlyList<KeyValuePair<string, string>> Encode(CartogramInfo c) => new List<KeyValuePair<string, string>>
        {
            Pair("name", c.Name ?? string.Empty),
            Pair("fact", c.FactSurfaceId ?? string.Empty),
            Pair("project", c.ProjectSurfaceId ?? string.Empty),
            Pair("boundary", c.BoundaryHandle ?? string.Empty),
            Pair("step", Codec.Number(c.Step)),
            Pair("ox", Codec.Number(c.OriginX)),
            Pair("oy", Codec.Number(c.OriginY)),
            Pair("angle", Codec.Number(c.AngleDegrees)),
            Pair("columns", c.Columns.ToString()),
            Pair("rows", c.Rows.ToString()),
            Pair("scale", c.Scale.ToString(CultureInfo.InvariantCulture)),
            Pair("node_mm", Codec.Number(c.NodeTextMm)),
            Pair("volume_mm", Codec.Number(c.VolumeTextMm)),
            Pair("totals_mm", Codec.Number(c.TotalsTextMm)),
            Pair("volume_dec", c.VolumeDecimals.ToString(CultureInfo.InvariantCulture)),
            Pair("built", c.Built ? "1" : "0"),
            Pair("built_fp", c.BuiltFingerprint ?? string.Empty),
            Pair("fill", Codec.Number(c.LastFill)),
            Pair("cut", Codec.Number(c.LastCut)),
            Pair("built_at", c.BuiltAt ?? string.Empty),
        };

        public static CartogramInfo Decode(string id, IReadOnlyDictionary<string, string> map)
        {
            string Text(string key) => map.TryGetValue(key, out string value) && value.Length > 0 ? value : null;
            var c = new CartogramInfo
            {
                Id = id,
                Name = Text("name") ?? "Картограмма",
                FactSurfaceId = Text("fact"),
                ProjectSurfaceId = Text("project"),
                BoundaryHandle = Text("boundary"),
                Step = Codec.Optional(map, "step") ?? 20,
                OriginX = Codec.Optional(map, "ox"),
                OriginY = Codec.Optional(map, "oy"),
                AngleDegrees = Codec.Optional(map, "angle") ?? 0,
                NodeTextMm = Codec.Optional(map, "node_mm") ?? 1.8,
                VolumeTextMm = Codec.Optional(map, "volume_mm") ?? 2.5,
                TotalsTextMm = Codec.Optional(map, "totals_mm") ?? 2.5,
                Built = Text("built") == "1",
                BuiltFingerprint = Text("built_fp"),
                LastFill = Codec.Optional(map, "fill") ?? 0,
                LastCut = Codec.Optional(map, "cut") ?? 0,
                BuiltAt = Text("built_at"),
            };
            if (Enum.TryParse(Text("columns"), out LabelScheme columns)) c.Columns = columns;
            if (Enum.TryParse(Text("rows"), out LabelScheme rows)) c.Rows = rows;
            if (int.TryParse(Text("scale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale) && KzmSettings.Scales.Contains(scale)) c.Scale = scale;
            if (int.TryParse(Text("volume_dec"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec)) c.VolumeDecimals = dec;
            return c;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    internal static class Codec
    {
        internal static string Number(double? value) =>
            value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

        internal static double? Optional(IReadOnlyDictionary<string, string> map, string key)
        {
            if (!map.TryGetValue(key, out string text) || text.Length == 0) return null;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !Guard.Finite(value))
                throw new KzmException("значение " + key + " в чертеже повреждено: «" + text + "»");
            return value;
        }
    }

    /// <summary>Stable 64-bit FNV-1a fingerprint of data (to tell whether a cartogram is outdated).</summary>
    public sealed class Fingerprint
    {
        private ulong hash = 14695981039346656037UL;

        public Fingerprint Add(string text)
        {
            foreach (char c in text ?? string.Empty)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            hash ^= 0xFF;
            hash *= 1099511628211UL;
            return this;
        }

        /// <summary>Values are rounded to 1 mm / 1e-6 so that harmless float noise does not count.</summary>
        public Fingerprint Add(double value) => Add(Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture));

        public override string ToString() => hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
