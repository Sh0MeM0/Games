using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Profiles.Core
{
    public static class ProfileEngine
    {
        public static DrawingPlan Build(ProfileProject project)
        {
            // Interpolate only a copy, so blank cells remain derived values after anchors change.
            ProfileValidation.Check(project, geometry: false);
            var enteredLongitudinal = project.Longitudinal;
            if (project.Kind == ProfileKind.Longitudinal)
                project = ProfileInterpolation.ResolveLongitudinal(project);
            ProfileValidation.Check(project);
            var elevations = new List<double>();
            var trapezoids = new Dictionary<CrossSection, List<Point2>>();
            if (project.Kind == ProfileKind.Longitudinal)
            {
                foreach (LongitudinalPoint point in project.Longitudinal)
                    elevations.AddRange(Values(point, project.Template));
            }
            else
            {
                foreach (CrossSection section in project.Sections)
                {
                    foreach (SectionPoint point in section.Points)
                    {
                        if (point.Existing.HasValue) elevations.Add(point.Existing.Value);
                        if (point.Design.HasValue) elevations.Add(point.Design.Value);
                    }
                    if (section.Water.HasValue) elevations.Add(section.Water.Value);
                    if (section.ProjectBottom.HasValue)
                    {
                        List<Point2> trapezoid = SectionGeometry.Trapezoid(section);
                        trapezoids.Add(section, trapezoid);
                        elevations.AddRange(trapezoid.Select(p => p.Y));
                    }
                }
            }
            if (elevations.Count == 0) ProfileValidation.Fail("Введите хотя бы одну отметку: пустые ячейки не создают линию на нуле.");
            double minimum = elevations.Min();
            double datum = project.Settings.Datum ?? (Math.Floor(minimum) - 1);
            if (datum >= minimum) ProfileValidation.Fail("Условная отметка должна быть ниже всех введённых отметок; минимальная отметка " + F(minimum) + " м.");
            var plan = new DrawingPlan { Datum = datum };
            var builder = new PlanBuilder(project.Settings, plan, project.Template);
            if (project.Kind == ProfileKind.Longitudinal) builder.Longitudinal(project.Longitudinal, enteredLongitudinal);
            else
            {
                double? previousBottom = null;
                foreach (CrossSection section in project.Sections)
                {
                    List<Point2> trapezoid;
                    trapezoids.TryGetValue(section, out trapezoid);
                    var sectionPlan = new DrawingPlan { Datum = datum };
                    new PlanBuilder(project.Settings, sectionPlan, project.Template).Section(section, trapezoid);
                    double minY = Bounds(sectionPlan.Entities, false), maxY = Bounds(sectionPlan.Entities, true);
                    double shift = previousBottom.HasValue ? previousBottom.Value - 20 * builder.Layout - maxY : 0;
                    foreach (DrawEntity entity in sectionPlan.Entities) Shift(entity, shift);
                    plan.Entities.AddRange(sectionPlan.Entities);
                    plan.Warnings.AddRange(sectionPlan.Warnings);
                    previousBottom = minY + shift;
                }
            }
            foreach (DrawEntity entity in plan.Entities)
                foreach (Point2 point in EntityPoints(entity))
                    if (!NumberParser.Finite(point.X) || !NumberParser.Finite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12)
                        ProfileValidation.Fail("Размер профиля слишком велик. Проверьте масштаб, единицы и условную отметку.");
            if (plan.Entities.Count > 250000) ProfileValidation.Fail("Профиль превышает 250000 объектов; разделите его на несколько проектов.");
            return plan;
        }

        private static IEnumerable<double> Values(LongitudinalPoint p, ProfileTemplate template)
        {
            var values = template == ProfileTemplate.Nvk ? new[] { p.Ground, p.ProjectGround, p.PipeInvert } :
                template == ProfileTemplate.NewChannel ? new[] { p.Ground, p.ProjectBottom } :
                new[] { p.Ground, p.ExistingBottom, p.ProjectBottom, p.LeftBank, p.RightBank, p.Water };
            return values.Where(x => x.HasValue).Select(x => x.Value);
        }
        internal static string F(double value) { return value.ToString("F2", CultureInfo.InvariantCulture); }
        internal static string Station(double value)
        {
            double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            if (rounded < 0) return F(rounded);
            double picket = Math.Floor(rounded / 100);
            return picket.ToString("0", CultureInfo.InvariantCulture) + "+" + (rounded - picket * 100).ToString("00.00", CultureInfo.InvariantCulture);
        }
        private static IEnumerable<Point2> EntityPoints(DrawEntity entity)
        {
            var line = entity as DrawPolyline;
            if (line != null) return line.Points;
            var text = entity as DrawText;
            if (text != null) return new[] { text.Position };
            var circle = (DrawCircle)entity;
            return new[] { new Point2(circle.Center.X, circle.Center.Y - circle.Radius), new Point2(circle.Center.X, circle.Center.Y + circle.Radius) };
        }
        private static double Bounds(List<DrawEntity> entities, bool upper)
        {
            return upper ? entities.Max(e => EntityPoints(e).Max(p => p.Y) + (e is DrawText ? TextHalfExtent((DrawText)e) : 0))
                : entities.Min(e => EntityPoints(e).Min(p => p.Y) - (e is DrawText ? TextHalfExtent((DrawText)e) : 0));
        }
        private static double TextHalfExtent(DrawText text)
        {
            double advance = text.Height * 0.75 * text.WidthFactor;
            string[] lines = text.Text.Split('\n');
            double width = lines.Max(line => line.Length) * advance;
            double rows = text.MaxWidth > 0 ? lines.Sum(line => Math.Max(1, Math.Ceiling(line.Length * advance / text.MaxWidth) + 1)) : lines.Length;
            if (text.MaxWidth > 0) width = Math.Min(width, text.MaxWidth);
            double radians = text.RotationDegrees * Math.PI / 180;
            return (Math.Abs(Math.Cos(radians)) * rows * text.Height * 1.3 + Math.Abs(Math.Sin(radians)) * width) / 2 + text.Height / 2;
        }
        private static void Shift(DrawEntity entity, double dy)
        {
            var line = entity as DrawPolyline;
            if (line != null) { line.Points = line.Points.Select(p => new Point2(p.X, p.Y + dy)).ToList(); return; }
            var text = entity as DrawText;
            if (text != null) { text.Position = new Point2(text.Position.X, text.Position.Y + dy); return; }
            var circle = (DrawCircle)entity;
            circle.Center = new Point2(circle.Center.X, circle.Center.Y + dy);
        }

        private sealed class Row
        {
            internal string Label, Kind;
            internal double Height;
            internal Func<LongitudinalPoint, double?> Value;
            internal Func<LongitudinalPoint, string> TextValue;
            internal short Color;
            internal Row(string label, double height, string kind = "", Func<LongitudinalPoint, double?> value = null, short color = 7)
            { Label = label; Height = height; Kind = kind; Value = value; Color = color; }
        }

        private sealed class PlanBuilder
        {
            private readonly ProfileSettings settings;
            private readonly DrawingPlan plan;
            private readonly ProfileTemplate template;
            internal double Paper { get; }
            internal double Layout { get { return Paper * Math.Max(1, settings.TextHeightMm / 2.5); } }
            private double TextHeight { get { return settings.TextHeightMm * Paper; } }
            private double X(double metres) { return metres / settings.MetresPerDrawingUnit; }
            private double Y(double elevation) { return (elevation - plan.Datum) * settings.HorizontalScale / (settings.MetresPerDrawingUnit * settings.VerticalScale); }
            internal PlanBuilder(ProfileSettings settings, DrawingPlan plan, ProfileTemplate template)
            { this.settings = settings; this.plan = plan; this.template = template; Paper = settings.HorizontalScale / (1000 * settings.MetresPerDrawingUnit); }

            private void Line(Point2 a, Point2 b, string layer = "PROF_GRID", short color = 8, double weight = 0.18)
            { plan.Entities.Add(new DrawPolyline { Layer = layer, ColorIndex = color, LineWeightMm = weight, Points = new List<Point2> { a, b } }); }

            private void Text(double x, double y, string value, double rotation = 0, short color = 7, TextAlignment alignment = TextAlignment.Center, double width = 0)
            {
                if (string.IsNullOrEmpty(value)) return;
                plan.Entities.Add(new DrawText { Layer = "PROF_TEXT", ColorIndex = color, Position = new Point2(x, y), Text = value,
                    Height = TextHeight, WidthFactor = settings.TextWidthFactor, RotationDegrees = rotation, Alignment = alignment, MaxWidth = Math.Max(0, width) });
            }

            private void Curve(List<Point2> points, string layer, short color, double weight = 0.18, bool dashed = false)
            {
                if (points.Count >= 2) plan.Entities.Add(new DrawPolyline { Layer = layer, ColorIndex = color, LineWeightMm = weight, Dashed = dashed, Points = points });
            }

            private void Header(string title, double labelWidth, double width, double highest)
            {
                double titleWidth = Math.Max(labelWidth + width, 100 * Layout);
                double titleHeight = WrappedHeight(title, titleWidth);
                Text(-labelWidth, highest + 20 * Layout + titleHeight / 2, title, alignment: TextAlignment.Left, width: titleWidth);
                Text(-labelWidth, highest + 16 * Layout, "ГОР. 1:" + settings.HorizontalScale.ToString("0.###", CultureInfo.InvariantCulture), alignment: TextAlignment.Left, width: labelWidth);
                Text(-labelWidth, highest + 12 * Layout, "ВЕРТ. 1:" + settings.VerticalScale.ToString("0.###", CultureInfo.InvariantCulture), alignment: TextAlignment.Left, width: labelWidth);
                Text(-labelWidth + 2 * Layout, 3 * Layout, "УСЛ. ОТМ. " + F(plan.Datum), alignment: TextAlignment.Left, width: labelWidth - 2 * Layout);
            }

            private double WrappedHeight(string value, double width)
            {
                // Conservative estimate: allow an extra line for word wrapping in the current drawing font.
                return value.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length * TextHeight * 0.75 * settings.TextWidthFactor / width) + 1)) * TextHeight * 1.3;
            }

            private double LabelHeight(string value, double width)
            {
                return value.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length * TextHeight * 0.75 * settings.TextWidthFactor / width))) * TextHeight * 1.2 + 2 * Layout;
            }

            internal void Longitudinal(List<LongitudinalPoint> points, List<LongitudinalPoint> entered)
            {
                double first = points[0].Station, width = X(points.Last().Station - first), labels = (template == ProfileTemplate.Nvk ? 60 : 55) * Layout;
                var rows = LongitudinalRows(points);
                var xs = points.Select(p => X(p.Station - first)).ToList();
                if (template == ProfileTemplate.Nvk)
                {
                    DrawLongCurve(points, xs, p => p.Ground, "PROF_GROUND", 7, 0.3, true);
                    DrawLongCurve(points, xs, p => p.ProjectGround, "PROF_PROJECT_GROUND", 1, 0.4);
                    DrawLongCurve(points, xs, p => p.PipeInvert, "PROF_PIPE_INVERT", 1, 0.3);
                }
                else
                {
                    DrawLongCurve(points, xs, p => p.Ground, "PROF_GROUND", 7);
                    DrawLongCurve(points, xs, p => p.ProjectBottom, "PROF_PROJECT", 1, 0.5);
                    if (template == ProfileTemplate.Reconstruction)
                    {
                        DrawLongCurve(points, xs, p => p.ExistingBottom, "PROF_EXISTING", 7, 0.3);
                        DrawLongCurve(points, xs, p => p.Water, "PROF_WATER", 5, 0.3, true);
                        DrawLongCurve(points, xs, p => p.LeftBank, "PROF_LEFT_BANK", 7, 0.3);
                        DrawLongCurve(points, xs, p => p.RightBank, "PROF_RIGHT_BANK", 6, 0.3, true);
                    }
                }
                for (int i = 0; i < points.Count; i++)
                {
                    List<double> values = Values(points[i], template).ToList();
                    if (values.Count > 0) Line(new Point2(xs[i], 0), new Point2(xs[i], Y(values.Max())), "PROF_ORD");
                }
                double y = 0;
                Line(new Point2(-labels, 0), new Point2(width, 0));
                foreach (Row row in rows)
                {
                    double bottom = y - LongRowHeight(row, points, labels - 4 * Layout, width), center = (y + bottom) / 2;
                    Line(new Point2(-labels, bottom), new Point2(width, bottom));
                    bool slopeRow = row.Kind == "slope" || row.Kind == "pipeSlope";
                    if (slopeRow)
                    {
                        SlopeHeading(labels, y, bottom, row.Kind == "pipeSlope");
                        SlopeAnchors(entered, first, y, bottom, row.Kind == "pipeSlope");
                    }
                    else Text(-labels + 2 * Layout, center, row.Label, alignment: TextAlignment.Left, width: labels - 4 * Layout);
                    if (!slopeRow && row.Kind == "plan") Line(new Point2(0, center), new Point2(width, center));
                    else if (!slopeRow)
                    {
                        bool uniformRatio = row.Kind == "ratio" && Ratio(points[0]) != "" && points.Select(Ratio).Distinct().Count() == 1;
                        bool uniform = row.Kind == "constant" && points.All(p => row.Value(p).HasValue) && points.Select(p => row.Value(p).Value).Distinct().Count() == 1;
                        bool uniformText = row.Kind == "text" && row.TextValue(points[0]) != "" && points.Select(row.TextValue).Distinct().Count() == 1;
                        if (uniformText) Text(width / 2, center, row.TextValue(points[0]), width: Math.Max(width, 10 * Layout));
                        else if (uniformRatio) Text(width / 2, center, Ratio(points[0]));
                        else if (uniform) Text(width / 2, center, F(row.Value(points[0]).Value));
                        else for (int i = 0; i < points.Count; i++)
                        {
                            Line(new Point2(xs[i], y), new Point2(xs[i], bottom));
                            if (row.Kind == "station") Text(xs[i], center, Station(points[i].Station), 90);
                            else if (row.Kind == "ratio") Text(xs[i], center, Ratio(points[i]), 90);
                            else if (row.Kind == "point") Text(xs[i], center, PointName(points[i], i), 90);
                            else if (row.TextValue != null) Text(xs[i], center, row.TextValue(points[i]), 90);
                            else if (row.Value != null && row.Value(points[i]).HasValue) Text(xs[i], center, F(row.Value(points[i]).Value), 90, row.Color);
                        }
                        for (int i = 1; i < points.Count; i++)
                        {
                            if (row.Kind == "distance") Text((xs[i - 1] + xs[i]) / 2, center, F(points[i].Station - points[i - 1].Station));
                        }
                    }
                    y = bottom;
                }
                foreach (double x in new[] { -labels, 0, width }) Line(new Point2(x, 0), new Point2(x, y));
                double highest = Y(points.SelectMany(point => Values(point, template)).Max());
                Header(settings.Title, labels, width, highest);
                if (template == ProfileTemplate.Nvk && width >= 75 * Layout) NvkLegend(width, highest, points.Any(point => point.PipeInvert.HasValue));
                if (points.Zip(points.Skip(1), (a, b) => X(b.Station - a.Station) < 5 * Layout).Any(x => x))
                    plan.Warnings.Add("Есть интервалы слишком узкие для выбранного текста: подписи могут пересекаться. Уменьшите знаменатель горизонтального масштаба или разделите профиль.");
                if (template == ProfileTemplate.Reconstruction && points.Any(p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue && p.ProjectBottom > p.ExistingBottom))
                    plan.Warnings.Add("Проектное дно местами выше существующего: глубина подчистки показана как 0.00 по исходному шаблону; объём засыпки не вычислялся.");
            }

            private void AddRow(List<Row> rows, List<LongitudinalPoint> points, string label, double height, Func<LongitudinalPoint, double?> value, short color = 7, string kind = "")
            { if (settings.ShowEmptyRows || points.Any(p => value(p).HasValue)) rows.Add(new Row(label, height, kind, value, color)); }

            private List<Row> LongitudinalRows(List<LongitudinalPoint> points)
            {
                if (template == ProfileTemplate.Nvk)
                    return new List<Row>
                    {
                        new Row("ОТМЕТКА НИЗА ИЛИ\nЛОТКА ТРУБЫ", 15, value: p => p.PipeInvert, color: 1),
                        new Row("ПРОЕКТНАЯ ОТМЕТКА\nЗЕМЛИ", 15, value: p => p.ProjectGround, color: 1),
                        new Row("НАТУРНАЯ ОТМЕТКА\nЗЕМЛИ", 15, value: p => p.Ground),
                        new Row("ОБОЗНАЧЕНИЕ ТРУБЫ\nИ ТИП ИЗОЛЯЦИИ", 15, "text") { TextValue = p => p.PipeDescription },
                        new Row("ОСНОВАНИЕ", 10, "text") { TextValue = p => p.Foundation },
                        new Row("УКЛОН, % / ДЛИНА, М", 10, "pipeSlope"),
                        new Row("РАССТОЯНИЕ, М", 10, "distance"),
                        new Row("НОМЕР КОЛОДЦА, ТОЧКИ,\nУГЛА ПОВОРОТА", 10, "point")
                    };

                bool reconstruction = template == ProfileTemplate.Reconstruction;
                var rows = new List<Row> { new Row("П Л А Н", 18, "plan"), new Row("П И К Е Т Ы", 14, "station"), new Row("РАССТОЯНИЯ, М", 5, "distance") };
                AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М", 12, p => p.Ground);
                if (reconstruction)
                {
                    AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ПРАВЫЙ БЕРЕГ)", 14, p => p.RightBank);
                    AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ЛЕВЫЙ БЕРЕГ)", 14, p => p.LeftBank);
                    AddRow(rows, points, "ОТМЕТКА УРОВНЯ ВОДЫ, М", 12, p => p.Water, 5);
                    AddRow(rows, points, "ГЛУБИНА ПОДЧИСТКИ, М", 7, p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue ? Math.Max(0, p.ExistingBottom.Value - p.ProjectBottom.Value) : (double?)null);
                    if (settings.ShowEmptyRows) rows.Add(new Row("ПЛОЩАДЬ ПОПЕРЕЧНОГО СЕЧЕНИЯ, М2", 8));
                }
                else AddRow(rows, points, "ГЛУБИНА ТОРФА, М", 7, p => p.PeatDepth);
                if (settings.ShowEmptyRows || points.Any(p => p.ProjectBottom.HasValue)) rows.Add(new Row("УКЛОН, ‰ / ДЛИНА, М", 12, "slope"));
                AddRow(rows, points, reconstruction ? "ОТМЕТКА ПРОЕКТНОЙ ПОДЧИСТКИ, М" : "ОТМЕТКА ПРОЕКТНОГО ДНА, М", 12, p => p.ProjectBottom, 1);
                if (reconstruction) AddRow(rows, points, "ОТМЕТКА СУЩЕСТВУЮЩЕГО ДНА, М", 12, p => p.ExistingBottom);
                AddRow(rows, points, "ШИРИНА ПО ДНУ, М", 6, p => p.BottomWidth, kind: "constant");
                if (settings.ShowEmptyRows || points.Any(p => p.LeftSlope.HasValue || p.RightSlope.HasValue))
                    rows.Add(new Row("ЗАЛОЖЕНИЕ ОТКОСОВ (Л/П)", 6, "ratio"));
                if (settings.ShowEmptyRows)
                {
                    if (!reconstruction) rows.Add(new Row("ПЛОЩАДЬ ПОПЕРЕЧНОГО СЕЧЕНИЯ, М2", 12));
                    rows.Add(new Row(reconstruction ? "ОБЪЕМ ЗЕМ. РАБОТ ПО ЗАСЫПКЕ, М3" : "ОБЪЕМ ЗЕМ. РАБОТ, М3", 12));
                    plan.Warnings.Add("Площадь сечения и объём земляных работ не рассчитываются; строки оставлены пустыми.");
                }
                return rows;
            }

            private static string PointName(LongitudinalPoint point, int index)
            { return string.IsNullOrWhiteSpace(point.PointName) ? (index + 1).ToString(CultureInfo.InvariantCulture) : point.PointName; }

            private void NvkLegend(double width, double highest, bool hasPipe)
            {
                var entries = new[] { "Проектная земля", "Натурная земля", "Низ / лоток трубы" };
                for (int index = 0; index < (hasPipe ? 3 : 2); index++)
                {
                    double y = highest + (16 - 4 * index) * Layout;
                    Curve(new List<Point2> { new Point2(0, y), new Point2(12 * Layout, y) }, "PROF_LEGEND",
                        index == 1 ? (short)7 : (short)1, index == 0 ? 0.4 : 0.3, index == 1);
                    Text(15 * Layout, y, entries[index], alignment: TextAlignment.Left, width: Math.Min(width - 15 * Layout, 60 * Layout));
                }
            }

            private void SlopeHeading(double labels, double top, double bottom, bool percent)
            {
                double height = top - bottom;
                Line(new Point2(-labels, percent ? top : bottom), new Point2(0, percent ? bottom : top));
                Text(percent ? -labels * 0.43 : -labels + 2 * Layout, top - .27 * height,
                    percent ? "УКЛОН, %" : "УКЛОН, ‰", alignment: TextAlignment.Left, width: labels * .4);
                Text(percent ? -labels + 2 * Layout : -labels * .33, bottom + .27 * height,
                    "ДЛИНА, М", alignment: TextAlignment.Left, width: labels * .3);
            }

            private void SlopeAnchors(List<LongitudinalPoint> entered, double first, double top, double bottom, bool pipe)
            {
                Func<LongitudinalPoint, double?> value = pipe ? (Func<LongitudinalPoint, double?>)(p => p.PipeInvert) : p => p.ProjectBottom;
                var anchors = entered.Where(point => value(point).HasValue).ToList();
                foreach (var anchor in anchors)
                {
                    double x = X(anchor.Station - first);
                    Line(new Point2(x, top), new Point2(x, bottom));
                }
                for (int index = 1; index < anchors.Count; index++)
                {
                    var left = anchors[index - 1];
                    var right = anchors[index];
                    Slope(X(left.Station - first), X(right.Station - first), top, bottom,
                        right.Station - left.Station, value(right).Value - value(left).Value, pipe ? 100 : 1000);
                }
            }

            private static string Ratio(LongitudinalPoint point)
            {
                if (!point.LeftSlope.HasValue && !point.RightSlope.HasValue) return "";
                if (point.LeftSlope.HasValue && point.RightSlope.HasValue && point.LeftSlope.Value == point.RightSlope.Value) return F(point.LeftSlope.Value);
                return (point.LeftSlope.HasValue ? F(point.LeftSlope.Value) : "—") + " / " + (point.RightSlope.HasValue ? F(point.RightSlope.Value) : "—");
            }

            private double LongRowHeight(Row row, List<LongitudinalPoint> points, double labelWidth, double width)
            {
                IEnumerable<string> rotated = Enumerable.Empty<string>();
                bool uniformText = row.Kind == "text" && row.TextValue != null && row.TextValue(points[0]) != "" && points.Select(row.TextValue).Distinct().Count() == 1;
                if (row.Kind == "station") rotated = points.Select(p => Station(p.Station));
                else if (row.Kind == "point") rotated = points.Select((point, index) => PointName(point, index));
                else if (row.TextValue != null && !uniformText)
                    rotated = points.Select(row.TextValue);
                else if (row.Kind == "ratio" && points.Select(Ratio).Distinct().Count() > 1) rotated = points.Select(Ratio);
                else if (row.Value != null && !(row.Kind == "constant" && points.All(p => row.Value(p).HasValue) && points.Select(p => row.Value(p).Value).Distinct().Count() == 1))
                    rotated = points.Where(p => row.Value(p).HasValue).Select(p => F(row.Value(p).Value));
                double textHeight = uniformText ? WrappedHeight(row.TextValue(points[0]), Math.Max(width, 10 * Layout)) + 2 * Layout : RotatedHeight(rotated);
                return Math.Max(Math.Max(row.Height * Layout, textHeight), LabelHeight(row.Label, labelWidth));
            }

            private double RotatedHeight(IEnumerable<string> values)
            {
                return values.Select(value => value.Length * TextHeight * 0.75 * settings.TextWidthFactor + 2 * Layout).DefaultIfEmpty(0).Max();
            }

            private void DrawLongCurve(List<LongitudinalPoint> points, List<double> xs, Func<LongitudinalPoint, double?> value, string layer, short color, double weight = 0.18, bool dashed = false)
            {
                var run = new List<Point2>();
                bool missing = false, present = false;
                for (int i = 0; i < points.Count; i++)
                {
                    double? z = value(points[i]);
                    if (z.HasValue) { present = true; run.Add(new Point2(xs[i], Y(z.Value))); }
                    else { missing = true; Curve(run, layer, color, weight, dashed); run = new List<Point2>(); }
                }
                Curve(run, layer, color, weight, dashed);
                if (missing && present) plan.Warnings.Add(layer + ": нет двух опорных отметок для крайних пустых ячеек; экстраполяция не выполнялась.");
            }

            private void Slope(double left, double right, double top, double bottom, double distance, double dz, double units = 1000)
            {
                double slope = Math.Abs(dz) * units / distance, length = Math.Sqrt(distance * distance + dz * dz), dx = right - left;
                string slopeLabel = slope.ToString(units == 1000 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
                if (Math.Abs(dz) < 1e-12)
                {
                    Line(new Point2(left, (top + bottom) / 2), new Point2(right, (top + bottom) / 2), "PROF_SLOPE", 7, 0.3);
                    Text((left + right) / 2, top - 0.27 * (top - bottom), slopeLabel);
                    Text((left + right) / 2, bottom + 0.27 * (top - bottom), F(length));
                    return;
                }
                Line(new Point2(left, dz < 0 ? top : bottom), new Point2(right, dz < 0 ? bottom : top), "PROF_SLOPE", 7, 0.3);
                Text(left + (dz < 0 ? 0.67 : 0.33) * dx, top - 0.27 * (top - bottom), slopeLabel);
                Text(left + (dz < 0 ? 0.33 : 0.67) * dx, bottom + 0.27 * (top - bottom), F(length));
            }

            internal void Section(CrossSection section, List<Point2> trapezoid)
            {
                double minX = section.Points[0].Offset, width = X(section.Points.Last().Offset - minX), labels = 38 * Layout;
                Func<Point2, Point2> transform = p => new Point2(X(p.X - minX), Y(p.Y));
                List<List<Point2>> existing = SectionGeometry.Runs(section.Points, false);
                List<List<Point2>> design = trapezoid != null ? new List<List<Point2>> { trapezoid } : SectionGeometry.Runs(section.Points, true);
                foreach (List<Point2> run in existing) Curve(run.Select(transform).ToList(), "PROF_EXISTING", 7, 0.3);
                foreach (List<Point2> run in design) Curve(run.Select(transform).ToList(), "PROF_PROJECT", 1, 0.5);
                if (section.Water.HasValue)
                {
                    List<List<Point2>> boundary = existing;
                    if (!existing.Any(run => run.Count >= 2))
                    {
                        boundary = design;
                        plan.Warnings.Add("Поперечник «" + section.Name + "»: нет непрерывного натурного участка; вода ограничена проектным профилем.");
                    }
                    List<List<Point2>> water = SectionGeometry.Water(boundary, section.Water.Value);
                    foreach (List<Point2> run in water) Curve(run.Select(transform).ToList(), "PROF_WATER", 5, 0.3, true);
                    if (water.Count == 0) plan.Warnings.Add("Поперечник «" + section.Name + "»: уровень воды не образует затопленного участка на доступных данных; линия воды не построена.");
                    else if (water.Any(run => Math.Abs(run[0].X - minX) < 1e-9 || Math.Abs(run.Last().X - section.Points.Last().Offset) < 1e-9))
                        plan.Warnings.Add("Поперечник «" + section.Name + "»: вода достигает границы съёмки; её ширина ограничена измеренным участком.");
                }
                var rows = new List<Tuple<string, double, string>> { Tuple.Create("№ ТОЧКИ", 6.0, "number"), Tuple.Create("РАССТОЯНИЕ, м", 5.0, "distance") };
                if (settings.ShowEmptyRows || existing.Count > 0) rows.Add(Tuple.Create("ОТМЕТКИ\nПОВЕРХНОСТИ, м", 12.0, "existing"));
                if (settings.ShowEmptyRows || design.Count > 0) rows.Add(Tuple.Create("ПРОЕКТНЫЕ\nОТМЕТКИ, м", 12.0, "design"));
                var heights = rows.Select(row => Math.Max(Math.Max(row.Item2 * Layout, LabelHeight(row.Item1, 35 * Layout)),
                    row.Item3 == "existing" ? RotatedHeight(section.Points.Where(p => p.Existing.HasValue).Select(p => F(p.Existing.Value))) :
                    row.Item3 == "design" ? RotatedHeight(section.Points.Select(p => trapezoid != null ? SectionGeometry.Interpolate(trapezoid, p.Offset) : p.Design).Where(z => z.HasValue).Select(z => F(z.Value))) : 0)).ToList();
                double bottomY = -heights.Sum(), top = 0;
                int rowIndex = 0;
                Line(new Point2(-labels, 0), new Point2(width, 0));
                foreach (var row in rows)
                {
                    double bottom = top - heights[rowIndex++], center = (top + bottom) / 2;
                    Line(new Point2(-labels, bottom), new Point2(width, bottom));
                    Text(-labels + 1.5 * Layout, center, row.Item1, alignment: TextAlignment.Left, width: labels - 3 * Layout);
                    for (int i = 0; i < section.Points.Count; i++)
                    {
                        SectionPoint point = section.Points[i];
                        double x = X(point.Offset - minX);
                        if (row.Item3 == "number") Text(x, center, (i + 1).ToString(CultureInfo.InvariantCulture));
                        else if (row.Item3 == "distance" && i > 0) Text(X((point.Offset + section.Points[i - 1].Offset) / 2 - minX), center, F(point.Offset - section.Points[i - 1].Offset));
                        else if (row.Item3 == "existing" && point.Existing.HasValue) Text(x, center, F(point.Existing.Value), 90);
                        else if (row.Item3 == "design")
                        {
                            double? value = trapezoid != null ? SectionGeometry.Interpolate(trapezoid, point.Offset) : point.Design;
                            if (value.HasValue) Text(x, center, F(value.Value), 90, 1);
                        }
                    }
                    top = bottom;
                }
                foreach (double x in new[] { -labels, 0, width }) Line(new Point2(x, 0), new Point2(x, bottomY));
                foreach (SectionPoint point in section.Points)
                {
                    double? designZ = trapezoid != null ? SectionGeometry.Interpolate(trapezoid, point.Offset) : point.Design;
                    double maximum = new[] { point.Existing, designZ }.Where(z => z.HasValue).Select(z => Y(z.Value)).DefaultIfEmpty(0).Max();
                    double x = X(point.Offset - minX);
                    Line(new Point2(x, bottomY), new Point2(x, maximum), "PROF_ORD");
                }
                List<Point2> all = existing.Concat(design).SelectMany(x => x).ToList();
                double highest = all.Max(p => Y(p.Y));
                if (section.Water.HasValue) highest = Math.Max(highest, Y(section.Water.Value));
                string title = settings.Title + " — " + section.Name + (section.Station.HasValue ? "   ПК " + Station(section.Station.Value) : "");
                if (trapezoid != null)
                    highest = Annotation("Дно " + F(section.ProjectBottom.Value) + " м; b=" + F(section.BottomWidth.Value) + " м; откосы " + F(section.LeftSlope.Value) + "/" + F(section.RightSlope.Value), width, highest, 1);
                if (section.Water.HasValue) highest = Annotation("УРОВЕНЬ ВОДЫ " + F(section.Water.Value) + " м", width, highest, 5);
                Header(title, labels, width, highest);
                if (section.Points.Any(p => !p.Existing.HasValue) && section.Points.Any(p => p.Existing.HasValue))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: пропуски натурных отметок оставлены разрывами.");
                if (section.Points.Any(p => !p.Design.HasValue) && section.Points.Any(p => p.Design.HasValue))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: пропуски проектных отметок оставлены разрывами.");
                if (section.Points.Zip(section.Points.Skip(1), (a, b) => X(b.Offset - a.Offset) < 5 * Layout).Any(x => x))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: есть интервалы слишком узкие для выбранного текста; проверьте расположение подписей.");
            }

            private double Annotation(string value, double width, double highest, short color)
            {
                double boxWidth = Math.Max(width, 70 * Layout), height = WrappedHeight(value, boxWidth);
                Text(width / 2, highest + 3 * Layout + height / 2, value, color: color, width: boxWidth);
                return highest + 3 * Layout + height;
            }
        }
    }
}
