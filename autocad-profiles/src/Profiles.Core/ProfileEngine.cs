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
            ProfileValidation.Check(project);
            var elevations = new List<double>();
            var trapezoids = new Dictionary<CrossSection, List<Point2>>();
            if (project.Kind == ProfileKind.Longitudinal)
            {
                foreach (LongitudinalPoint point in project.Longitudinal)
                    elevations.AddRange(Values(point));
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
            var builder = new PlanBuilder(project.Settings, plan);
            if (project.Kind == ProfileKind.Longitudinal) builder.Longitudinal(project.Longitudinal);
            else
            {
                double? previousBottom = null;
                foreach (CrossSection section in project.Sections)
                {
                    List<Point2> trapezoid;
                    trapezoids.TryGetValue(section, out trapezoid);
                    var sectionPlan = new DrawingPlan { Datum = datum };
                    new PlanBuilder(project.Settings, sectionPlan).Section(section, trapezoid);
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

        private static IEnumerable<double> Values(LongitudinalPoint p)
        {
            return new[] { p.Ground, p.ExistingBottom, p.ProjectBottom, p.LeftBank, p.RightBank, p.Water }.Where(x => x.HasValue).Select(x => x.Value);
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
            internal short Color;
            internal Row(string label, double height, string kind = "", Func<LongitudinalPoint, double?> value = null, short color = 7)
            { Label = label; Height = height; Kind = kind; Value = value; Color = color; }
        }

        private sealed class PlanBuilder
        {
            private readonly ProfileSettings settings;
            private readonly DrawingPlan plan;
            internal double Paper { get; }
            internal double Layout { get { return Paper * Math.Max(1, settings.TextHeightMm / 2.5); } }
            private double TextHeight { get { return settings.TextHeightMm * Paper; } }
            private double X(double metres) { return metres / settings.MetresPerDrawingUnit; }
            private double Y(double elevation) { return (elevation - plan.Datum) * settings.HorizontalScale / (settings.MetresPerDrawingUnit * settings.VerticalScale); }
            internal PlanBuilder(ProfileSettings settings, DrawingPlan plan)
            { this.settings = settings; this.plan = plan; Paper = settings.HorizontalScale / (1000 * settings.MetresPerDrawingUnit); }

            private void Line(Point2 a, Point2 b, string layer = "PROF_GRID", short color = 8, double weight = 0.18)
            { plan.Entities.Add(new DrawPolyline { Layer = layer, ColorIndex = color, LineWeightMm = weight, Points = new List<Point2> { a, b } }); }

            private void Text(double x, double y, string value, double rotation = 0, short color = 7, TextAlignment alignment = TextAlignment.Center, double width = 0)
            {
                if (string.IsNullOrEmpty(value)) return;
                plan.Entities.Add(new DrawText { Layer = "PROF_TEXT", ColorIndex = color, Position = new Point2(x, y), Text = value,
                    Height = TextHeight, WidthFactor = settings.TextWidthFactor, RotationDegrees = rotation, Alignment = alignment, MaxWidth = Math.Max(0, width) });
            }

            private void Curve(List<Point2> points, string layer, short color, double weight = 0.18, bool dashed = false, bool markVertices = false)
            {
                if (points.Count >= 2) plan.Entities.Add(new DrawPolyline { Layer = layer, ColorIndex = color, LineWeightMm = weight, Dashed = dashed, Points = points });
                if (points.Count == 1 || markVertices)
                    foreach (Point2 p in points) plan.Entities.Add(new DrawCircle { Layer = layer, ColorIndex = color, LineWeightMm = weight, Center = p, Radius = 0.55 * Paper });
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

            internal void Longitudinal(List<LongitudinalPoint> points)
            {
                double first = points[0].Station, width = X(points.Last().Station - first), labels = 55 * Layout;
                var rows = new List<Row> { new Row("П Л А Н", 18, "plan"), new Row("П И К Е Т Ы", 14, "station"), new Row("РАССТОЯНИЯ, М", 5, "distance") };
                AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М", 12, p => p.Ground);
                AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ПРАВЫЙ БЕРЕГ)", 14, p => p.RightBank, 30);
                AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ЛЕВЫЙ БЕРЕГ)", 14, p => p.LeftBank, 3);
                AddRow(rows, points, "ОТМЕТКА УРОВНЯ ВОДЫ, М", 12, p => p.Water, 5);
                bool recon = points.Any(p => p.ExistingBottom.HasValue || p.LeftBank.HasValue || p.RightBank.HasValue || p.Water.HasValue);
                if (recon) AddRow(rows, points, "ГЛУБИНА ПОДЧИСТКИ, М", 7, p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue ? Math.Max(0, p.ExistingBottom.Value - p.ProjectBottom.Value) : (double?)null);
                else if (settings.ShowEmptyRows) rows.Add(new Row("ГЛУБИНА ТОРФА, М", 7));
                if (recon && settings.ShowEmptyRows) rows.Add(new Row("ПЛОЩАДЬ ПОПЕРЕЧНОГО СЕЧЕНИЯ, М2", 8));
                if (settings.ShowEmptyRows || points.Any(p => p.ProjectBottom.HasValue)) rows.Add(new Row("УКЛОН, ‰ / ДЛИНА, М", 12, "slope"));
                AddRow(rows, points, recon ? "ОТМЕТКА ПРОЕКТНОЙ ПОДЧИСТКИ, М" : "ОТМЕТКА ПРОЕКТНОГО ДНА, М", 12, p => p.ProjectBottom, 1);
                AddRow(rows, points, "ОТМЕТКА СУЩЕСТВУЮЩЕГО ДНА, М", 12, p => p.ExistingBottom);
                AddRow(rows, points, "ШИРИНА ПО ДНУ, М", 6, p => p.BottomWidth, kind: "constant");
                if (settings.ShowEmptyRows || points.Any(p => p.LeftSlope.HasValue || p.RightSlope.HasValue))
                    rows.Add(new Row("ЗАЛОЖЕНИЕ ОТКОСОВ (Л/П)", 6, "ratio"));
                if (settings.ShowEmptyRows)
                {
                    if (!recon) rows.Add(new Row("ПЛОЩАДЬ ПОПЕРЕЧНОГО СЕЧЕНИЯ, М2", 12));
                    rows.Add(new Row(recon ? "ОБЪЕМ ЗЕМ. РАБОТ ПО ЗАСЫПКЕ, М3" : "ОБЪЕМ ЗЕМ. РАБОТ, М3", 12));
                    plan.Warnings.Add("Площадь сечения и объём земляных работ не рассчитываются; строки оставлены пустыми.");
                }
                var xs = points.Select(p => X(p.Station - first)).ToList();
                DrawLongCurve(points, xs, p => p.Ground, "PROF_GROUND", 7);
                DrawLongCurve(points, xs, p => p.ExistingBottom, "PROF_EXISTING", 7);
                DrawLongCurve(points, xs, p => p.ProjectBottom, "PROF_PROJECT", 1, 0.5);
                DrawLongCurve(points, xs, p => p.Water, "PROF_WATER", 5);
                DrawLongCurve(points, xs, p => p.LeftBank, "PROF_LEFT_BANK", 3, 0.3);
                DrawLongCurve(points, xs, p => p.RightBank, "PROF_RIGHT_BANK", 30, 0.3, true);
                for (int i = 0; i < points.Count; i++)
                {
                    List<double> values = Values(points[i]).ToList();
                    if (values.Count > 0) Line(new Point2(xs[i], 0), new Point2(xs[i], Y(values.Max())), "PROF_ORD");
                }
                double y = 0;
                Line(new Point2(-labels, 0), new Point2(width, 0));
                foreach (Row row in rows)
                {
                    double bottom = y - LongRowHeight(row, points), center = (y + bottom) / 2;
                    Line(new Point2(-labels, bottom), new Point2(width, bottom));
                    Text(-labels + 2 * Layout, center, row.Label, alignment: TextAlignment.Left, width: labels - 4 * Layout);
                    if (row.Kind == "plan") Line(new Point2(0, center), new Point2(width, center));
                    else
                    {
                        bool uniformRatio = row.Kind == "ratio" && Ratio(points[0]) != "" && points.Select(Ratio).Distinct().Count() == 1;
                        bool uniform = row.Kind == "constant" && points.All(p => row.Value(p).HasValue) && points.Select(p => row.Value(p).Value).Distinct().Count() == 1;
                        if (uniformRatio) Text(width / 2, center, Ratio(points[0]));
                        else if (uniform) Text(width / 2, center, F(row.Value(points[0]).Value));
                        else for (int i = 0; i < points.Count; i++)
                        {
                            Line(new Point2(xs[i], y), new Point2(xs[i], bottom));
                            if (row.Kind == "station") Text(xs[i], center, Station(points[i].Station), 90);
                            else if (row.Kind == "ratio") Text(xs[i], center, Ratio(points[i]), 90);
                            else if (row.Value != null && row.Value(points[i]).HasValue) Text(xs[i], center, F(row.Value(points[i]).Value), 90, row.Color);
                        }
                        for (int i = 1; i < points.Count; i++)
                        {
                            if (row.Kind == "distance") Text((xs[i - 1] + xs[i]) / 2, center, F(points[i].Station - points[i - 1].Station));
                            if (row.Kind == "slope" && points[i - 1].ProjectBottom.HasValue && points[i].ProjectBottom.HasValue)
                                Slope(xs[i - 1], xs[i], y, bottom, points[i].Station - points[i - 1].Station, points[i].ProjectBottom.Value - points[i - 1].ProjectBottom.Value);
                        }
                    }
                    y = bottom;
                }
                foreach (double x in new[] { -labels, 0, width }) Line(new Point2(x, 0), new Point2(x, y));
                Header(settings.Title, labels, width, Y(points.SelectMany(Values).Max()));
                if (points.Zip(points.Skip(1), (a, b) => X(b.Station - a.Station) < 5 * Layout).Any(x => x))
                    plan.Warnings.Add("Есть интервалы слишком узкие для выбранного текста: подписи могут пересекаться. Уменьшите знаменатель горизонтального масштаба или разделите профиль.");
                if (points.Any(p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue && p.ProjectBottom > p.ExistingBottom))
                    plan.Warnings.Add("Проектное дно местами выше существующего: глубина подчистки показана как 0.00 по исходному шаблону; объём засыпки не вычислялся.");
            }

            private void AddRow(List<Row> rows, List<LongitudinalPoint> points, string label, double height, Func<LongitudinalPoint, double?> value, short color = 7, string kind = "")
            { if (settings.ShowEmptyRows || points.Any(p => value(p).HasValue)) rows.Add(new Row(label, height, kind, value, color)); }

            private static string Ratio(LongitudinalPoint point)
            {
                if (!point.LeftSlope.HasValue && !point.RightSlope.HasValue) return "";
                if (point.LeftSlope.HasValue && point.RightSlope.HasValue && point.LeftSlope.Value == point.RightSlope.Value) return F(point.LeftSlope.Value);
                return (point.LeftSlope.HasValue ? F(point.LeftSlope.Value) : "—") + " / " + (point.RightSlope.HasValue ? F(point.RightSlope.Value) : "—");
            }

            private double LongRowHeight(Row row, List<LongitudinalPoint> points)
            {
                IEnumerable<string> rotated = Enumerable.Empty<string>();
                if (row.Kind == "station") rotated = points.Select(p => Station(p.Station));
                else if (row.Kind == "ratio" && points.Select(Ratio).Distinct().Count() > 1) rotated = points.Select(Ratio);
                else if (row.Value != null && !(row.Kind == "constant" && points.All(p => row.Value(p).HasValue) && points.Select(p => row.Value(p).Value).Distinct().Count() == 1))
                    rotated = points.Where(p => row.Value(p).HasValue).Select(p => F(row.Value(p).Value));
                return Math.Max(Math.Max(row.Height * Layout, RotatedHeight(rotated)), LabelHeight(row.Label, 51 * Layout));
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
                if (missing && present) plan.Warnings.Add(layer + ": пропущенные отметки оставлены разрывами, интерполяция не выполнялась.");
            }

            private void Slope(double left, double right, double top, double bottom, double distance, double dz)
            {
                double slope = Math.Abs(dz) * 1000 / distance, length = Math.Sqrt(distance * distance + dz * dz), dx = right - left;
                if (Math.Abs(dz) < 1e-12)
                {
                    Line(new Point2(left, (top + bottom) / 2), new Point2(right, (top + bottom) / 2));
                    Text((left + right) / 2, top - 0.27 * (top - bottom), F(0));
                    Text((left + right) / 2, bottom + 0.27 * (top - bottom), F(length));
                    return;
                }
                Line(new Point2(left, dz < 0 ? top : bottom), new Point2(right, dz < 0 ? bottom : top));
                Text(left + (dz < 0 ? 0.67 : 0.33) * dx, top - 0.27 * (top - bottom), F(slope));
                Text(left + (dz < 0 ? 0.33 : 0.67) * dx, bottom + 0.27 * (top - bottom), F(length));
            }

            internal void Section(CrossSection section, List<Point2> trapezoid)
            {
                double minX = section.Points[0].Offset, width = X(section.Points.Last().Offset - minX), labels = 38 * Layout;
                Func<Point2, Point2> transform = p => new Point2(X(p.X - minX), Y(p.Y));
                List<List<Point2>> existing = SectionGeometry.Runs(section.Points, false);
                List<List<Point2>> design = trapezoid != null ? new List<List<Point2>> { trapezoid } : SectionGeometry.Runs(section.Points, true);
                foreach (List<Point2> run in existing) Curve(run.Select(transform).ToList(), "PROF_EXISTING", 7, markVertices: true);
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
                    foreach (List<Point2> run in water) Curve(run.Select(transform).ToList(), "PROF_WATER", 5);
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
                    highest = Annotation("Дно " + F(section.ProjectBottom.Value) + " м; b=" + F(section.BottomWidth.Value) + " м; откосы " + F(section.LeftSlope.Value) + "/" + F(section.RightSlope.Value), width, highest, 7);
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
