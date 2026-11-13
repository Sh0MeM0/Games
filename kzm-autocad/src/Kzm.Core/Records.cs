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
                Pair("tin_ref", surface.TrianglesBlockHandle ?? string.Empty),
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
                TrianglesBlockHandle = map.TryGetValue("tin_ref", out string tinRef) && tinRef.Length > 0 ? tinRef : null,
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
        /// <summary>Подписи в узле (задание п. 4): угол, слой, цвет. По умолчанию — как в примере Б.4 СТБ 2073-2010.</summary>
        public NodeLabelStyle WorkingLabel { get; set; } = NodeLabelStyle.DefaultWorking();
        public NodeLabelStyle ProjectLabel { get; set; } = NodeLabelStyle.DefaultProject();
        public NodeLabelStyle FactLabel { get; set; } = NodeLabelStyle.DefaultFact();
        /// <summary>Контрольный расчёт по наложению триангуляций (задание п. 5).</summary>
        public bool ControlCheck { get; set; } = true;
        /// <summary>При пересчёте ставить подписи, сдвинутые вручную, туда же (задание п. 7).</summary>
        public bool KeepMovedLabels { get; set; } = true;
        public bool Built { get; set; }
        public string BuiltFingerprint { get; set; }
        public double LastFill { get; set; }
        public double LastCut { get; set; }
        public string BuiltAt { get; set; }
        public double? LastControlFill { get; set; }
        public double? LastControlCut { get; set; }
        public double? LastArea { get; set; }
        public int LastSquares { get; set; }

        public double UnitsPerMm => Scale / 1000.0;

        public CartogramInfo Clone()
        {
            var copy = (CartogramInfo)MemberwiseClone();
            copy.WorkingLabel = WorkingLabel.Clone();
            copy.ProjectLabel = ProjectLabel.Clone();
            copy.FactLabel = FactLabel.Clone();
            return copy;
        }

        public IEnumerable<(string Title, NodeLabelStyle Style)> Labels()
        {
            yield return ("рабочая", WorkingLabel);
            yield return ("проектная", ProjectLabel);
            yield return ("фактическая", FactLabel);
        }

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
            foreach (var (title, style) in Labels())
                if (!NodeLabelStyle.IsValidLayer(style.Layer)) problems.Add("слой подписи (" + title + ") задан неверно: «" + style.Layer + "»");
            var corners = Labels().GroupBy(l => l.Style.Corner).Where(g => g.Count() > 1).ToList();
            foreach (var group in corners)
                problems.Add("подписи " + string.Join(" и ", group.Select(l => l.Title)) + " стоят в одном углу узла");
            return problems;
        }

        /// <summary>
        /// Parameters the drawing depends on (for «устарела»). New parameters are added only when they
        /// differ from their defaults, so cartograms built by version 0.4 are not reported outdated.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> FingerprintPairs(CartogramInfo c)
        {
            var skip = new HashSet<string> { "name", "built", "built_fp", "fill", "cut", "built_at", "control", "keep_moved", "cfill", "ccut", "area", "squares" };
            var defaults = new CartogramInfo();
            var result = new List<KeyValuePair<string, string>>();
            foreach (var pair in Encode(c))
            {
                if (skip.Contains(pair.Key)) continue;
                if (pair.Key == "lbl_w" && c.WorkingLabel.Same(defaults.WorkingLabel)) continue;
                if (pair.Key == "lbl_p" && c.ProjectLabel.Same(defaults.ProjectLabel)) continue;
                if (pair.Key == "lbl_f" && c.FactLabel.Same(defaults.FactLabel)) continue;
                result.Add(pair);
            }
            return result;
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
            Pair("lbl_w", c.WorkingLabel.Encode()),
            Pair("lbl_p", c.ProjectLabel.Encode()),
            Pair("lbl_f", c.FactLabel.Encode()),
            Pair("control", c.ControlCheck ? "1" : "0"),
            Pair("keep_moved", c.KeepMovedLabels ? "1" : "0"),
            Pair("cfill", Codec.Number(c.LastControlFill)),
            Pair("ccut", Codec.Number(c.LastControlCut)),
            Pair("area", Codec.Number(c.LastArea)),
            Pair("squares", c.LastSquares.ToString(CultureInfo.InvariantCulture)),
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
            c.WorkingLabel = NodeLabelStyle.Decode(Text("lbl_w"), c.WorkingLabel);
            c.ProjectLabel = NodeLabelStyle.Decode(Text("lbl_p"), c.ProjectLabel);
            c.FactLabel = NodeLabelStyle.Decode(Text("lbl_f"), c.FactLabel);
            c.ControlCheck = Text("control") != "0";
            c.KeepMovedLabels = Text("keep_moved") != "0";
            c.LastControlFill = Codec.Optional(map, "cfill");
            c.LastControlCut = Codec.Optional(map, "ccut");
            c.LastArea = Codec.Optional(map, "area");
            if (int.TryParse(Text("squares"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int squares)) c.LastSquares = squares;
            return c;
        }

        /// <summary>Parameters of a new cartogram taken from the template (step, label schemes, appearance).</summary>
        public void TakeTemplate(CartogramInfo template)
        {
            if (template == null) return;
            Step = template.Step;
            Columns = template.Columns;
            Rows = template.Rows;
            Scale = template.Scale;
            NodeTextMm = template.NodeTextMm;
            VolumeTextMm = template.VolumeTextMm;
            TotalsTextMm = template.TotalsTextMm;
            VolumeDecimals = template.VolumeDecimals;
            WorkingLabel = template.WorkingLabel.Clone();
            ProjectLabel = template.ProjectLabel.Clone();
            FactLabel = template.FactLabel.Clone();
            ControlCheck = template.ControlCheck;
            KeepMovedLabels = template.KeepMovedLabels;
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }

    public enum LabelCorner
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    }

    /// <summary>Одна из трёх подписей узла: угол относительно узла, слой, цвет («BYLAYER», «ACI:n», «RGB:r,g,b»).</summary>
    public sealed class NodeLabelStyle
    {
        public const string WorkingLayer = "КЗМ_ОТМ_РАБ";
        public const string ProjectLayer = "КЗМ_ОТМ_ПРОЕКТ";
        public const string FactLayer = "КЗМ_ОТМ_ФАКТ";

        public LabelCorner Corner { get; set; }
        public string Layer { get; set; }
        public string Color { get; set; } = PointAppearance.ByLayer;

        public static NodeLabelStyle DefaultWorking() => new NodeLabelStyle { Corner = LabelCorner.TopLeft, Layer = WorkingLayer };
        public static NodeLabelStyle DefaultProject() => new NodeLabelStyle { Corner = LabelCorner.TopRight, Layer = ProjectLayer };
        public static NodeLabelStyle DefaultFact() => new NodeLabelStyle { Corner = LabelCorner.BottomRight, Layer = FactLayer };

        public NodeLabelStyle Clone() => (NodeLabelStyle)MemberwiseClone();

        public bool Same(NodeLabelStyle other) =>
            other != null && Corner == other.Corner && string.Equals(Layer, other.Layer, StringComparison.Ordinal) &&
            string.Equals(Color ?? PointAppearance.ByLayer, other.Color ?? PointAppearance.ByLayer, StringComparison.Ordinal);

        public static string Title(LabelCorner corner)
        {
            switch (corner)
            {
                case LabelCorner.TopLeft: return "слева сверху";
                case LabelCorner.TopRight: return "справа сверху";
                case LabelCorner.BottomLeft: return "слева снизу";
                default: return "справа снизу";
            }
        }

        /// <summary>Characters AutoCAD does not allow in layer names: &lt; &gt; / \ " : ; ? * | , = `.</summary>
        public static bool IsValidLayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return false;
            return name.IndexOfAny("<>/\\\":;?*|,=`".ToCharArray()) < 0 && name.Trim() == name;
        }

        internal string Encode() => ((int)Corner).ToString(CultureInfo.InvariantCulture) + "|" + (Layer ?? string.Empty) + "|" + (Color ?? PointAppearance.ByLayer);

        internal static NodeLabelStyle Decode(string text, NodeLabelStyle fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var parts = text.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int corner) ||
                corner < 0 || corner > 3 || parts[1].Length == 0)
                return fallback;
            return new NodeLabelStyle { Corner = (LabelCorner)corner, Layer = parts[1], Color = parts[2].Length > 0 ? parts[2] : PointAppearance.ByLayer };
        }
    }

    public enum StructureLineKind
    {
        /// <summary>Структурная линия по полилинии (задание п. 1, 2.3).</summary>
        Line = 0,
        /// <summary>Заданная диагональ пары треугольников (задание п. 3, «поменять диагональ»).</summary>
        Flip = 1,
    }

    /// <summary>
    /// Структурная линия поверхности: последовательность точек поверхности (handles блоков
    /// «КЗМ_ТОЧКА»); соседние точки соединяются рёбрами-ограничениями триангуляции.
    /// </summary>
    public sealed class StructureLine
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public StructureLineKind Kind { get; set; }
        public bool Closed { get; set; }
        /// <summary>Polyline the line was made from (for information).</summary>
        public string SourceHandle { get; set; }
        public List<string> PointHandles { get; } = new List<string>();

        /// <summary>Consecutive pairs of the given (alive) points; the closing pair for a closed line.</summary>
        public IEnumerable<(string A, string B)> Segments(Func<string, bool> alive)
        {
            var points = PointHandles.Where(h => alive == null || alive(h)).ToList();
            for (int i = 0; i + 1 < points.Count; i++) yield return (points[i], points[i + 1]);
            if (Closed && points.Count > 2) yield return (points[points.Count - 1], points[0]);
        }

        public static IReadOnlyList<KeyValuePair<string, string>> Encode(StructureLine line)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("name", line.Name ?? string.Empty),
                new KeyValuePair<string, string>("kind", line.Kind == StructureLineKind.Flip ? "FLIP" : "LINE"),
                new KeyValuePair<string, string>("closed", line.Closed ? "1" : "0"),
                new KeyValuePair<string, string>("source", line.SourceHandle ?? string.Empty),
            };
            values.AddRange(line.PointHandles.Select(h => new KeyValuePair<string, string>("pt", h)));
            return values;
        }

        public static StructureLine Decode(string id, IEnumerable<KeyValuePair<string, string>> values)
        {
            var line = new StructureLine { Id = id };
            foreach (var pair in values)
            {
                switch (pair.Key)
                {
                    case "name": line.Name = pair.Value; break;
                    case "kind": line.Kind = pair.Value == "FLIP" ? StructureLineKind.Flip : StructureLineKind.Line; break;
                    case "closed": line.Closed = pair.Value == "1"; break;
                    case "source": line.SourceHandle = string.IsNullOrEmpty(pair.Value) ? null : pair.Value; break;
                    case "pt": if (!string.IsNullOrEmpty(pair.Value)) line.PointHandles.Add(pair.Value); break;
                }
            }
            if (string.IsNullOrEmpty(line.Name)) line.Name = line.Kind == StructureLineKind.Flip ? "Диагональ" : "Линия";
            return line;
        }

        /// <summary>Part of the surface fingerprint; empty when the surface has no lines (as in version 0.4).</summary>
        public static string FingerprintOf(IEnumerable<StructureLine> lines)
        {
            var list = lines.OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
            if (list.Count == 0) return string.Empty;
            var fingerprint = new Fingerprint();
            foreach (var line in list)
            {
                fingerprint.Add(line.Id).Add(line.Kind.ToString()).Add(line.Closed ? "c" : "o");
                foreach (var handle in line.PointHandles) fingerprint.Add(handle);
            }
            return fingerprint.ToString();
        }
    }

    /// <summary>
    /// Шаблон по умолчанию (задание п. 8: «настройки хранятся в чертеже и как шаблон по умолчанию»):
    /// настройки чертежа и параметры новой картограммы, текстом «ключ=значение».
    /// </summary>
    public static class DefaultsCodec
    {
        private static readonly string[] TemplateKeys =
        {
            "step", "columns", "rows", "scale", "node_mm", "volume_mm", "totals_mm", "volume_dec", "lbl_w", "lbl_p", "lbl_f", "control", "keep_moved",
        };

        public static string Encode(KzmSettings settings, CartogramInfo template)
        {
            var lines = new List<string> { "# КЗМ: шаблон по умолчанию", "version=1" };
            foreach (var pair in SettingsCodec.Encode(settings).Where(p => p.Key == "decimals" || p.Key == "scale"))
                lines.Add("settings." + pair.Key + "=" + pair.Value);
            foreach (var pair in CartogramInfo.Encode(template).Where(p => TemplateKeys.Contains(p.Key)))
                lines.Add("cartogram." + pair.Key + "=" + pair.Value);
            return string.Join("\n", lines) + "\n";
        }

        public static (KzmSettings Settings, CartogramInfo Template) Decode(string text)
        {
            var settings = new List<KeyValuePair<string, string>>();
            var cartogram = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                if (key == "version" && value != "1") throw new KzmException("шаблон по умолчанию записан более новой версией модуля");
                if (key.StartsWith("settings.", StringComparison.Ordinal))
                    settings.Add(new KeyValuePair<string, string>(key.Substring(9), value));
                else if (key.StartsWith("cartogram.", StringComparison.Ordinal) && TemplateKeys.Contains(key.Substring(10)))
                    cartogram[key.Substring(10)] = value;
            }
            var decodedSettings = SettingsCodec.Decode(settings);
            var template = CartogramInfo.Decode("template", cartogram);
            var problems = template.Validate().Where(p => !p.Contains("имя") && !p.Contains("поверхность") && !p.Contains("граница")).ToList();
            if (problems.Count > 0) throw new KzmException("шаблон по умолчанию повреждён: " + string.Join("; ", problems));
            return (decodedSettings, template);
        }
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
