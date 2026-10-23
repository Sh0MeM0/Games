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
            else
            {
                project = ProjectFile.Clone(project);
                project.Sections = project.Sections.Select(section => ProfileInterpolation.ResolveSection(section, project.Longitudinal)).ToList();
            }
            ProfileValidation.Check(project);
            var elevations = new List<double>();
            var trapezoids = new Dictionary<CrossSection, List<Point2>>();
            var sectionEarthworks = new Dictionary<CrossSection, SectionEarthworks>();
            EarthworksSummary earthworks = null;
            if (project.Kind == ProfileKind.Longitudinal)
            {
                foreach (LongitudinalPoint point in project.Longitudinal)
                    elevations.AddRange(Values(point, project.Template));
                earthworks = Earthworks.CalculateVolumes(project);
            }
            else
            {
                foreach (CrossSection section in project.Sections)
                {
                    var result = Earthworks.Calculate(section, project.Longitudinal);
                    sectionEarthworks.Add(section, result);
                    elevations.AddRange(result.CleanedRuns.SelectMany(run => run).Select(point => point.Y));
                    foreach (SectionPoint point in section.Points)
                    {
                        if (point.Existing.HasValue) elevations.Add(point.Existing.Value);
                        if (point.Design.HasValue) elevations.Add(point.Design.Value);
                    }
                    if (section.Water.HasValue) elevations.Add(section.Water.Value);
                    if (section.ProjectBottom.HasValue)
                    {
                        List<Point2> trapezoid = result.DesignCurve;
                        if (trapezoid.Count < 2)
                            ProfileValidation.Fail("Поперечник «" + section.Name + "»: " + result.Status);
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
            if (project.Kind == ProfileKind.Longitudinal)
            {
                plan.Warnings.AddRange(earthworks.Warnings);
                builder.Longitudinal(project.Longitudinal, enteredLongitudinal, earthworks);
            }
            else
            {
                double? previousBottom = null;
                foreach (CrossSection section in project.Sections)
                {
                    List<Point2> trapezoid;
                    trapezoids.TryGetValue(section, out trapezoid);
                    var sectionPlan = new DrawingPlan { Datum = datum };
                    new PlanBuilder(project.Settings, sectionPlan, project.Template).Section(section, trapezoid, sectionEarthworks[section]);
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
            var hatch = entity as DrawHatch;
            if (hatch != null) return hatch.Boundary;
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
            var hatch = entity as DrawHatch;
            if (hatch != null) { hatch.Boundary = hatch.Boundary.Select(p => new Point2(p.X, p.Y + dy)).ToList(); return; }
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
            internal List<Tuple<double, double?>> StationValues;
            internal short Color;
            internal Row(string label, double height, string kind = "", Func<LongitudinalPoint, double?> value = null, short color = 7)
            { Label = label; Height = height; Kind = kind; Value = value; Color = color; }
        }

        private sealed class PlanBuilder
        {
            private readonly ProfileSettings settings;
            private readonly DrawingPlan plan;
            private readonly ProfileTemplate template;
            private readonly List<Tuple<double, double, double, double>> calloutBoxes = new List<Tuple<double, double, double, double>>();
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

            private void Header(string title, double labelWidth, double width, double highest, bool longitudinal = false, bool hasPipe = false)
            {
                if (longitudinal) highest += LongitudinalLegend(labelWidth, highest, hasPipe);
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

            internal void Longitudinal(List<LongitudinalPoint> points, List<LongitudinalPoint> entered, EarthworksSummary earthworks)
            {
                double first = points[0].Station, width = X(points.Last().Station - first), labels = (template == ProfileTemplate.Nvk ? 60 : 55) * Layout;
                var rows = LongitudinalRows(points, earthworks);
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
                    bool volumeRow = row.Kind.StartsWith("volume", StringComparison.Ordinal);
                    if (slopeRow)
                    {
                        SlopeHeading(labels, y, bottom, row.Kind == "pipeSlope");
                        SlopeAnchors(entered, first, y, bottom, row.Kind == "pipeSlope");
                    }
                    else Text(-labels + 2 * Layout, center, row.Label, alignment: TextAlignment.Left, width: labels - 4 * Layout);
                    if (volumeRow) VolumeIntervals(earthworks, row.Kind, first, points.Last().Station, y, bottom);
                    else if (row.Kind == "area") AreaStations(row, first, points.Last().Station, y, bottom);
                    else if (!slopeRow && row.Kind == "plan") Line(new Point2(0, center), new Point2(width, center));
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
                Header(settings.Title, labels, width, highest, true, points.Any(point => point.PipeInvert.HasValue));
                if (points.Zip(points.Skip(1), (a, b) => X(b.Station - a.Station) < 5 * Layout).Any(x => x))
                    plan.Warnings.Add("Есть интервалы слишком узкие для выбранного текста: подписи могут пересекаться. Уменьшите знаменатель горизонтального масштаба или разделите профиль.");
                if (template == ProfileTemplate.Reconstruction && points.Any(p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue && p.ProjectBottom > p.ExistingBottom))
                    plan.Warnings.Add("Проектное дно местами выше существующего: глубина подчистки показана как 0.00. Объёмы досыпки определяются по площадям поперечников.");
                if (earthworks.Stations.Any(item => item.Station < first - 1e-8 || item.Station > points.Last().Station + 1e-8))
                    plan.Warnings.Add("Есть поперечники за пределами продольного профиля: их площади и выходящие за границы интервалы объёмов не показаны в этой таблице.");
            }

            private void AddRow(List<Row> rows, List<LongitudinalPoint> points, string label, double height, Func<LongitudinalPoint, double?> value, short color = 7, string kind = "")
            { if (settings.ShowEmptyRows || points.Any(p => value(p).HasValue)) rows.Add(new Row(label, height, kind, value, color)); }

            private List<Row> LongitudinalRows(List<LongitudinalPoint> points, EarthworksSummary earthworks)
            {
                if (template == ProfileTemplate.Nvk)
                    return AddEarthworkRows(new List<Row>
                    {
                        new Row("ОТМЕТКА НИЗА ИЛИ\nЛОТКА ТРУБЫ", 15, value: p => p.PipeInvert, color: 1),
                        new Row("ПРОЕКТНАЯ ОТМЕТКА\nЗЕМЛИ", 15, value: p => p.ProjectGround, color: 1),
                        new Row("НАТУРНАЯ ОТМЕТКА\nЗЕМЛИ", 15, value: p => p.Ground),
                        new Row("ОБОЗНАЧЕНИЕ ТРУБЫ\nИ ТИП ИЗОЛЯЦИИ", 15, "text") { TextValue = p => p.PipeDescription },
                        new Row("ОСНОВАНИЕ", 10, "text") { TextValue = p => p.Foundation },
                        new Row("УКЛОН, % / ДЛИНА, М", 10, "pipeSlope"),
                        new Row("РАССТОЯНИЕ, М", 10, "distance"),
                        new Row("НОМЕР КОЛОДЦА, ТОЧКИ,\nУГЛА ПОВОРОТА", 10, "point")
                    }, earthworks);

                bool reconstruction = template == ProfileTemplate.Reconstruction;
                var rows = new List<Row> { new Row("П Л А Н", 18, "plan"), new Row("П И К Е Т Ы", 14, "station"), new Row("РАССТОЯНИЯ, М", 5, "distance") };
                AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М", 12, p => p.Ground);
                if (reconstruction)
                {
                    AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ПРАВЫЙ БЕРЕГ)", 14, p => p.RightBank);
                    AddRow(rows, points, "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М\n(ЛЕВЫЙ БЕРЕГ)", 14, p => p.LeftBank);
                    AddRow(rows, points, "ОТМЕТКА УРОВНЯ ВОДЫ, М", 12, p => p.Water, 5);
                    AddRow(rows, points, "ГЛУБИНА ПОДЧИСТКИ, М", 7, p => p.ExistingBottom.HasValue && p.ProjectBottom.HasValue ? Math.Max(0, p.ExistingBottom.Value - p.ProjectBottom.Value) : (double?)null);
                }
                else AddRow(rows, points, "ГЛУБИНА ТОРФА, М", 7, p => p.PeatDepth);
                if (settings.ShowEmptyRows || points.Any(p => p.ProjectBottom.HasValue)) rows.Add(new Row("УКЛОН, ‰ / ДЛИНА, М", 12, "slope"));
                AddRow(rows, points, reconstruction ? "ОТМЕТКА ПРОЕКТНОЙ ПОДЧИСТКИ, М" : "ОТМЕТКА ПРОЕКТНОГО ДНА, М", 12, p => p.ProjectBottom, 1);
                if (reconstruction) AddRow(rows, points, "ОТМЕТКА СУЩЕСТВУЮЩЕГО ДНА, М", 12, p => p.ExistingBottom);
                AddRow(rows, points, "ШИРИНА ПО ДНУ, М", 6, p => p.BottomWidth, kind: "constant");
                if (settings.ShowEmptyRows || points.Any(p => p.LeftSlope.HasValue || p.RightSlope.HasValue))
                    rows.Add(new Row("ЗАЛОЖЕНИЕ ОТКОСОВ (Л/П)", 6, "ratio"));
                return AddEarthworkRows(rows, earthworks);
            }

            private List<Row> AddEarthworkRows(List<Row> rows, EarthworksSummary earthworks)
            {
                if (settings.ShowEmptyRows || earthworks.Stations.Count > 0)
                {
                    rows.Add(AreaRow("ПЛОЩАДЬ СРЕЗКИ, М²", earthworks, result => result.CutArea));
                    rows.Add(AreaRow("ПЛОЩАДЬ ДОСЫПКИ, М²", earthworks, result => result.FillArea));
                    rows.Add(AreaRow("ПЛОЩАДЬ ЗАИЛЕНИЯ, М²", earthworks, result => result.SiltArea));
                }
                rows.Add(new Row("ОБЪЁМ СРЕЗКИ, М³", 8, "volumeCut"));
                rows.Add(new Row("ОБЪЁМ ДОСЫПКИ, М³", 8, "volumeFill"));
                rows.Add(new Row("ОБЪЁМ ЗАИЛЕНИЯ, М³", 8, "volumeSilt"));
                return rows;
            }

            private static Row AreaRow(string label, EarthworksSummary earthworks, Func<SectionEarthworks, double?> value)
            {
                return new Row(label, 8, "area")
                {
                    StationValues = earthworks.Stations.Select(item => Tuple.Create(item.Station, item.Result == null ? null : value(item.Result))).ToList()
                };
            }

            private void AreaStations(Row row, double first, double last, double top, double bottom)
            {
                foreach (var value in row.StationValues)
                {
                    if (value.Item1 < first - 1e-8 || value.Item1 > last + 1e-8) continue;
                    double x = X(value.Item1 - first);
                    Line(new Point2(x, top), new Point2(x, bottom));
                    if (value.Item2.HasValue) Text(x, (top + bottom) / 2, F(value.Item2.Value), 90);
                }
            }

            private void VolumeIntervals(EarthworksSummary earthworks, string kind, double first, double last, double top, double bottom)
            {
                foreach (var interval in earthworks.Intervals)
                {
                    if (interval.StartStation < first - 1e-8 || interval.EndStation > last + 1e-8) continue;
                    double left = X(interval.StartStation - first), right = X(interval.EndStation - first);
                    Line(new Point2(left, top), new Point2(left, bottom));
                    Line(new Point2(right, top), new Point2(right, bottom));
                    double? value = kind == "volumeCut" ? interval.CutVolume : kind == "volumeFill" ? interval.FillVolume : interval.SiltVolume;
                    if (value.HasValue) Text((left + right) / 2, (top + bottom) / 2, F(value.Value));
                }
            }

            private static string PointName(LongitudinalPoint point, int index)
            { return string.IsNullOrWhiteSpace(point.PointName) ? (index + 1).ToString(CultureInfo.InvariantCulture) : point.PointName; }

            private double LongitudinalLegend(double labelWidth, double highest, bool hasPipe)
            {
                var labels = template == ProfileTemplate.Nvk ? new[] { "Проектная отм. земли", "Натурная отм. земли", "Низ / лоток трубы" } :
                    template == ProfileTemplate.NewChannel ? new[] { "Проектная отм. дна", "Существующая отм. земли" } :
                    new[] { "Проектная отм. дна", "Существующая отм. дна", "Левый берег", "Правый берег", "Уровень воды" };
                var colors = template == ProfileTemplate.Reconstruction ? new short[] { 1, 7, 7, 6, 5 } : new short[] { 1, 7, 1 };
                int count = template == ProfileTemplate.Nvk && !hasPipe ? 2 : labels.Length;
                Text(-labelWidth, highest + (count * 4 + 7) * Layout, "Условные обозначения", alignment: TextAlignment.Left, width: 70 * Layout);
                for (int index = 0; index < count; index++)
                {
                    double y = highest + ((count - index) * 4 + 1) * Layout;
                    bool dashed = template == ProfileTemplate.Reconstruction ? index >= 3 : template == ProfileTemplate.Nvk && index == 1;
                    Curve(new List<Point2> { new Point2(-labelWidth, y), new Point2(-labelWidth + 12 * Layout, y) }, "PROF_LEGEND",
                        colors[index], index == 0 ? (template == ProfileTemplate.Nvk ? 0.4 : 0.5) : 0.3, dashed);
                    Text(-labelWidth + 15 * Layout, y, labels[index], alignment: TextAlignment.Left, width: 65 * Layout);
                }
                return (count * 4 + 10) * Layout;
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
                else if (row.StationValues != null) rotated = row.StationValues.Where(item => item.Item2.HasValue).Select(item => F(item.Item2.Value));
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

            internal void Section(CrossSection section, List<Point2> trapezoid, SectionEarthworks earthworks)
            {
                double minX = section.Points[0].Offset, width = X(section.Points.Last().Offset - minX), labels = 38 * Layout;
                Func<Point2, Point2> transform = p => new Point2(X(p.X - minX), Y(p.Y));
                List<SectionPoint> display = SectionDisplayGeometry.Build(earthworks);
                List<List<Point2>> existing = SectionGeometry.Runs(section.Points, false);
                List<List<Point2>> design = trapezoid != null ? new List<List<Point2>> { trapezoid } : SectionGeometry.Runs(section.Points, true);
                var water = new List<List<Point2>>();
                HatchRegions(earthworks.CutRegions, transform, "PROF_CUT_HATCH", 1, 45);
                HatchRegions(earthworks.FillRegions, transform, "PROF_FILL_HATCH", 3, 135);
                HatchRegions(earthworks.SiltRegions, transform, "PROF_SILT_HATCH", 30, 45);
                if (earthworks.SiltRegions.Count > 0)
                    foreach (var run in earthworks.CleanedRuns)
                        Curve(run.Select(transform).ToList(), "PROF_CLEANED", 30, 0.25, true);
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
                    water = SectionGeometry.Water(boundary, section.Water.Value);
                    foreach (List<Point2> run in water) Curve(run.Select(transform).ToList(), "PROF_WATER", 5, 0.3, true);
                    if (water.Count == 0) plan.Warnings.Add("Поперечник «" + section.Name + "»: уровень воды не образует затопленного участка на доступных данных; линия воды не построена.");
                    else if (water.Any(run => Math.Abs(run[0].X - minX) < 1e-9 || Math.Abs(run.Last().X - section.Points.Last().Offset) < 1e-9))
                        plan.Warnings.Add("Поперечник «" + section.Name + "»: вода достигает границы съёмки; её ширина ограничена измеренным участком.");
                }
                var rows = new List<Tuple<string, double, string>> { Tuple.Create("№ ТОЧКИ", 6.0, "number"), Tuple.Create("РАССТОЯНИЕ, м", 5.0, "distance") };
                if (settings.ShowEmptyRows || existing.Count > 0) rows.Add(Tuple.Create("ОТМЕТКИ\nПОВЕРХНОСТИ, м", 12.0, "existing"));
                if (settings.ShowEmptyRows || design.Count > 0) rows.Add(Tuple.Create("ПРОЕКТНЫЕ\nОТМЕТКИ, м", 12.0, "design"));
                var cells = rows.Select(row => SectionCells(row.Item3, display, minX)).ToList();
                var heights = rows.Select((row, index) => Math.Max(Math.Max(row.Item2 * Layout, LabelHeight(row.Item1, 35 * Layout)),
                    SectionCellHeight(cells[index]) * cells[index].Select(cell => cell.Lane + 1).DefaultIfEmpty(1).Max())).ToList();
                double bottomY = -heights.Sum(), top = 0;
                int rowIndex = 0;
                Line(new Point2(-labels, 0), new Point2(width, 0));
                foreach (var row in rows)
                {
                    var rowCells = cells[rowIndex];
                    double bottom = top - heights[rowIndex++], center = (top + bottom) / 2;
                    Line(new Point2(-labels, bottom), new Point2(width, bottom));
                    Text(-labels + 1.5 * Layout, center, row.Item1, alignment: TextAlignment.Left, width: labels - 3 * Layout);
                    int laneCount = rowCells.Select(cell => cell.Lane + 1).DefaultIfEmpty(1).Max();
                    foreach (var cell in rowCells)
                        Text(cell.X, top - (top - bottom) * (cell.Lane + .5) / laneCount, cell.Text, cell.Rotation, cell.Color);
                    top = bottom;
                }
                foreach (double x in new[] { -labels, 0, width }) Line(new Point2(x, 0), new Point2(x, bottomY));
                foreach (SectionPoint point in display)
                {
                    double maximum = new[] { point.Existing, point.Design }.Where(z => z.HasValue).Select(z => Y(z.Value)).DefaultIfEmpty(0).Max();
                    double x = X(point.Offset - minX);
                    Line(new Point2(x, bottomY), new Point2(x, maximum), "PROF_ORD");
                }
                List<Point2> all = existing.Concat(design).SelectMany(x => x).ToList();
                double highest = all.Max(p => Y(p.Y));
                if (water.Count > 0) highest = Math.Max(highest, Y(section.Water.Value));
                Text(-labels + 2 * Layout, 9 * Layout, SectionIdentifier(section), alignment: TextAlignment.Left, width: labels - 2 * Layout);
                Text(-labels + 2 * Layout, 3 * Layout, "УСЛ. ОТМ. " + F(plan.Datum), alignment: TextAlignment.Left, width: labels - 2 * Layout);
                calloutBoxes.Add(Tuple.Create(-labels, 1 * Layout, labels, 10 * Layout));
                var callouts = new List<SectionCallout>
                {
                    AreaCallout("F срезки = " + Quantity(earthworks.CutArea) + " м²", earthworks.CutRegions, transform, 1),
                    AreaCallout("F досыпки = " + Quantity(earthworks.FillArea) + " м²", earthworks.FillRegions, transform, 3),
                    AreaCallout("F заиления = " + Quantity(earthworks.SiltArea) + " м²", earthworks.SiltRegions, transform, 30)
                };
                if (water.Count > 0)
                {
                    var run = water.OrderByDescending(points => points.Last().X - points[0].X).First();
                    callouts.Add(new SectionCallout { Text = "УРОВЕНЬ ВОДЫ " + F(section.Water.Value) + " м", Color = 5,
                        Target = transform(new Point2((run[0].X + run.Last().X) / 2, section.Water.Value)) });
                }
                SectionCallouts(callouts, width, highest);
                if (trapezoid != null && trapezoid.Count >= 4)
                {
                    SlopeCallout(transform(trapezoid[0]), transform(trapezoid[1]), section.LeftSlope.Value, true);
                    SlopeCallout(transform(trapezoid[trapezoid.Count - 2]), transform(trapezoid[trapezoid.Count - 1]), section.RightSlope.Value, false);
                }
                else
                    foreach (var run in design)
                    {
                        var simplified = StraightSegments(run);
                        for (int index = 1; index < simplified.Count; index++)
                        {
                            var a = simplified[index - 1];
                            var b = simplified[index];
                            double rise = b.Y - a.Y;
                            if (Math.Abs(rise) <= 1e-10) continue;
                            SlopeCallout(transform(a), transform(b), Math.Abs((b.X - a.X) / rise), rise < 0);
                        }
                    }
                if (!earthworks.IsValid && !string.IsNullOrWhiteSpace(earthworks.Status))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: " + earthworks.Status);
                if (section.Points.Any(p => !p.Existing.HasValue) && section.Points.Any(p => p.Existing.HasValue))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: пропуски натурных отметок оставлены разрывами.");
                if (section.Points.Any(p => !p.Design.HasValue) && section.Points.Any(p => p.Design.HasValue))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: пропуски проектных отметок оставлены разрывами.");
                if (cells.Any(row => row.Any(cell => cell.Lane > 0)))
                    plan.Warnings.Add("Поперечник «" + section.Name + "»: подписи близких точек разнесены по высоте внутри строк таблицы.");
            }

            private static string Quantity(double? value) { return value.HasValue ? F(value.Value) : "—"; }

            private sealed class SectionCell
            {
                internal double X, Rotation;
                internal string Text;
                internal short Color = 7;
                internal int Lane;
            }

            private List<SectionCell> SectionCells(string kind, List<SectionPoint> display, double first)
            {
                var cells = new List<SectionCell>();
                for (int index = 0; index < display.Count; index++)
                {
                    var point = display[index];
                    double x = X(point.Offset - first);
                    if (kind == "number") cells.Add(new SectionCell { X = x, Text = (index + 1).ToString(CultureInfo.InvariantCulture) });
                    else if (kind == "distance" && index > 0)
                        cells.Add(new SectionCell { X = X((point.Offset + display[index - 1].Offset) / 2 - first), Text = F(point.Offset - display[index - 1].Offset) });
                    else if (kind == "existing" && point.Existing.HasValue)
                        cells.Add(new SectionCell { X = x, Text = F(point.Existing.Value), Rotation = 90 });
                    else if (kind == "design" && point.Design.HasValue)
                        cells.Add(new SectionCell { X = x, Text = F(point.Design.Value), Rotation = 90, Color = 1 });
                }
                var laneEnds = new List<double>();
                foreach (var cell in cells)
                {
                    double halfWidth = (cell.Rotation == 90 ? TextHeight * 1.2 : cell.Text.Length * TextHeight * .75 * settings.TextWidthFactor) / 2;
                    int lane = laneEnds.FindIndex(end => end + .7 * Layout <= cell.X - halfWidth);
                    if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(double.NegativeInfinity); }
                    cell.Lane = lane;
                    laneEnds[lane] = cell.X + halfWidth;
                }
                return cells;
            }

            private double SectionCellHeight(List<SectionCell> cells)
            {
                return cells.Any(cell => cell.Rotation == 90) ? RotatedHeight(cells.Select(cell => cell.Text)) : TextHeight * 1.3 + 2 * Layout;
            }

            private void HatchRegions(List<List<Point2>> regions, Func<Point2, Point2> transform, string layer, short color, double angle)
            {
                foreach (var region in regions)
                    if (region.Count >= 3)
                    {
                        var boundary = region.Select(transform).ToList();
                        double radians = angle * Math.PI / 180;
                        var perpendicular = boundary.Select(point => -(point.X - boundary[0].X) * Math.Sin(radians) + (point.Y - boundary[0].Y) * Math.Cos(radians)).ToList();
                        double span = perpendicular.Max() - perpendicular.Min();
                        plan.Entities.Add(new DrawHatch
                        {
                            Layer = layer, ColorIndex = color, LineWeightMm = 0.13,
                            Boundary = boundary,
                            PatternSpacing = span > 0 ? Math.Min(1.2 * Paper, span / 3) : 1.2 * Paper, PatternAngleDegrees = angle
                        });
                    }
            }

            private sealed class SectionCallout
            {
                internal string Text;
                internal Point2? Target;
                internal short Color;
            }

            private static string SectionIdentifier(CrossSection section)
            {
                if (section.Station.HasValue) return Station(section.Station.Value);
                string name = (section.Name ?? "").Trim();
                return name.StartsWith("ПК", StringComparison.OrdinalIgnoreCase) ? name.Substring(2).Trim() : name;
            }

            private SectionCallout AreaCallout(string text, List<List<Point2>> regions, Func<Point2, Point2> transform, short color)
            {
                var region = regions.OrderByDescending(PolygonArea).FirstOrDefault();
                return new SectionCallout { Text = text, Color = color, Target = region == null ? (Point2?)null : transform(InteriorPoint(region)) };
            }

            private static double PolygonArea(List<Point2> polygon)
            {
                double twice = 0;
                var origin = polygon[0];
                for (int index = 0; index < polygon.Count; index++)
                {
                    var a = polygon[index];
                    var b = polygon[(index + 1) % polygon.Count];
                    twice += (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y);
                }
                return Math.Abs(twice) / 2;
            }

            private static Point2 InteriorPoint(List<Point2> polygon)
            {
                var xs = polygon.Select(point => point.X).Distinct().OrderBy(x => x).ToList();
                var best = polygon[0];
                double bestArea = -1;
                for (int index = 1; index < xs.Count; index++)
                {
                    double x = (xs[index - 1] + xs[index]) / 2;
                    var crossings = new List<double>();
                    for (int edge = 0; edge < polygon.Count; edge++)
                    {
                        var a = polygon[edge];
                        var b = polygon[(edge + 1) % polygon.Count];
                        if ((a.X < x && b.X > x) || (b.X < x && a.X > x))
                            crossings.Add(a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X));
                    }
                    crossings.Sort();
                    for (int crossing = 1; crossing < crossings.Count; crossing += 2)
                    {
                        double area = (xs[index] - xs[index - 1]) * (crossings[crossing] - crossings[crossing - 1]);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = new Point2(x, (crossings[crossing] + crossings[crossing - 1]) / 2);
                        }
                    }
                }
                return best;
            }

            private void SectionCallouts(List<SectionCallout> callouts, double width, double highest)
            {
                double labelWidth = callouts.Max(note => note.Text.Length) * TextHeight * .65 * settings.TextWidthFactor + 2 * Layout;
                double spread = Math.Max(width, 2 * labelWidth + 8 * Layout);
                double left = (width - spread) / 2;
                var sorted = callouts.OrderBy(note => note.Target.HasValue ? note.Target.Value.X : width / 2).ToList();
                int leftCount = (sorted.Count + 1) / 2;
                for (int index = 0; index < sorted.Count; index++)
                {
                    var note = sorted[index];
                    bool onLeft = index < leftCount;
                    int row = onLeft ? index : index - leftCount;
                    double x = onLeft ? left : left + spread - labelWidth;
                    double y = highest + (7 + row * 9) * Layout;
                    Leader(note.Text, note.Target, x, y, labelWidth, note.Color);
                }
            }

            private void SlopeCallout(Point2 a, Point2 b, double slope, bool left)
            {
                var target = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                double labelWidth = 12 * Layout;
                double x = target.X + (left ? -labelWidth - 5 * Layout : 5 * Layout);
                Leader("1:" + slope.ToString("0.###", CultureInfo.InvariantCulture), target, x, target.Y + 5 * Layout, labelWidth, 1);
            }

            private static List<Point2> StraightSegments(List<Point2> run)
            {
                var simplified = new List<Point2>();
                foreach (var point in run)
                {
                    while (simplified.Count >= 2)
                    {
                        var a = simplified[simplified.Count - 2];
                        var b = simplified[simplified.Count - 1];
                        double cross = (b.X - a.X) * (point.Y - b.Y) - (b.Y - a.Y) * (point.X - b.X);
                        double scale = Math.Abs((b.X - a.X) * (point.Y - b.Y)) + Math.Abs((b.Y - a.Y) * (point.X - b.X));
                        if (Math.Abs(cross) > 1e-10 * Math.Max(1, scale)) break;
                        simplified.RemoveAt(simplified.Count - 1);
                    }
                    simplified.Add(point);
                }
                return simplified;
            }

            private void Leader(string value, Point2? target, double x, double y, double width, short color)
            {
                width = Math.Max(width, value.Length * TextHeight * .65 * settings.TextWidthFactor);
                double labelHeight = TextHeight * 1.3;
                double preferredX = x, preferredY = y;
                var candidates = new List<Point2>();
                for (int ring = 0; ring <= 8; ring++)
                    for (int horizontal = -ring; horizontal <= ring; horizontal++)
                        for (int vertical = -ring; vertical <= ring; vertical++)
                            if (Math.Max(Math.Abs(horizontal), Math.Abs(vertical)) == ring)
                                candidates.Add(new Point2(preferredX + horizontal * (width + 4 * Layout), preferredY + vertical * 6 * Layout));
                var placement = candidates.Where(point => point.Y >= 2 * Layout)
                    .OrderBy(point => Math.Abs(point.X - preferredX) + 1.3 * Math.Abs(point.Y - preferredY))
                    .FirstOrDefault(point => LeaderFits(point.X, point.Y, width, labelHeight, target));
                if (placement.Y >= 2 * Layout)
                { x = placement.X; y = placement.Y; }
                else
                {
                    // Dense profiles can exhaust nearby positions; move outside all annotations.
                    x = Math.Max(preferredX, calloutBoxes.Select(box => box.Item1 + box.Item3).DefaultIfEmpty(preferredX).Max() + 4 * Layout);
                    y = Math.Max(y, 2 * Layout);
                    for (int attempt = 0; attempt < 32 && !LeaderFits(x, y, width, labelHeight, target); attempt++)
                    { x += width + 4 * Layout; y += 6 * Layout; }
                    if (!LeaderFits(x, y, width, labelHeight, target))
                        plan.Warnings.Add("Для выноски «" + value + "» недостаточно свободного места: проверьте её положение после построения.");
                }
                calloutBoxes.Add(Tuple.Create(x, y + 2 * Layout - labelHeight / 2, width, labelHeight));
                if (target.HasValue)
                {
                    bool connectRight = target.Value.X >= x + width / 2;
                    var elbow = new Point2(connectRight ? x + width : x, y);
                    var landing = new Point2(connectRight ? x : x + width, y);
                    Curve(new List<Point2> { target.Value, elbow, landing }, "PROF_LEADER", color, 0.18);
                    double dx = elbow.X - target.Value.X, dy = elbow.Y - target.Value.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length > 1e-12)
                    {
                        dx /= length; dy /= length;
                        double size = 1.5 * Layout;
                        Curve(new List<Point2> {
                            new Point2(target.Value.X + size * (dx - .3 * dy), target.Value.Y + size * (dy + .3 * dx)),
                            target.Value,
                            new Point2(target.Value.X + size * (dx + .3 * dy), target.Value.Y + size * (dy - .3 * dx))
                        }, "PROF_LEADER_ARROW", color, 0.18);
                    }
                }
                Text(x, y + 2 * Layout, value, color: color, alignment: TextAlignment.Left);
            }

            private bool LeaderFits(double x, double y, double width, double height, Point2? target)
            {
                var label = Tuple.Create(x - Layout, y + 2 * Layout - height / 2 - .7 * Layout, width + 2 * Layout, height + 1.4 * Layout);
                if (calloutBoxes.Any(box => RectanglesOverlap(label, box))) return false;
                foreach (var curve in plan.Entities.OfType<DrawPolyline>().Where(line =>
                    line.Layer == "PROF_EXISTING" || line.Layer == "PROF_PROJECT" || line.Layer == "PROF_CLEANED" || line.Layer == "PROF_WATER" || line.Layer == "PROF_LEADER"))
                    for (int index = 1; index < curve.Points.Count; index++)
                        if (SegmentMeetsBox(curve.Points[index - 1], curve.Points[index], label)) return false;
                if (!target.HasValue) return true;
                var elbow = new Point2(target.Value.X >= x + width / 2 ? x + width : x, y);
                var landing = new Point2(target.Value.X >= x + width / 2 ? x : x + width, y);
                var ownText = Tuple.Create(x, y + 2 * Layout - height / 2, width, height);
                if (SegmentMeetsBox(target.Value, elbow, ownText)) return false;
                return !calloutBoxes.Any(box => SegmentMeetsBox(target.Value, elbow, box) || SegmentMeetsBox(elbow, landing, box));
            }

            private static bool RectanglesOverlap(Tuple<double, double, double, double> a, Tuple<double, double, double, double> b)
            {
                return a.Item1 < b.Item1 + b.Item3 && a.Item1 + a.Item3 > b.Item1 && a.Item2 < b.Item2 + b.Item4 && a.Item2 + a.Item4 > b.Item2;
            }

            private static bool SegmentMeetsBox(Point2 a, Point2 b, Tuple<double, double, double, double> box)
            {
                double lower = 0, upper = 1;
                return ClipAxis(a.X, b.X - a.X, box.Item1, box.Item1 + box.Item3, ref lower, ref upper) &&
                    ClipAxis(a.Y, b.Y - a.Y, box.Item2, box.Item2 + box.Item4, ref lower, ref upper);
            }

            private static bool ClipAxis(double origin, double delta, double minimum, double maximum, ref double lower, ref double upper)
            {
                if (delta == 0) return origin >= minimum && origin <= maximum;
                double first = (minimum - origin) / delta, last = (maximum - origin) / delta;
                if (first > last) { double swap = first; first = last; last = swap; }
                lower = Math.Max(lower, first); upper = Math.Min(upper, last);
                return lower <= upper;
            }
        }
    }
}
