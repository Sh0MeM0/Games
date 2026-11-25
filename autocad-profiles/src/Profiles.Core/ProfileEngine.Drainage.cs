using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Profiles.Core
{
    public static partial class ProfileEngine
    {
        private sealed partial class PlanBuilder
        {
            // Longitudinal profile of a drainage collector. Every size is taken
            // from the reference sheets 095.26-РМС (листы 27, 28) in paper
            // millimetres at the standard 2.5 mm text and scales with Layout:
            // a 35 mm heading column, a 5 mm gap before ordinate 0, rows of
            // 5/5/10/10/10/10 mm, 0.25 mm lines and a 0.40 mm red drain line.
            private const double DrainHeading = 35, DrainGap = 5;
            private static readonly double[] DrainRows = { 5, 5, 10, 10, 10, 10 };
            private const double DrainLine = 0.25, DrainWeight = 0.40;
            // The reference sheets use ISOCPEUR at width factor 1.0 (glyph advances measured in the PDF).
            private const string DrainFont = "isocpeur.ttf";

            internal void Drainage(List<LongitudinalPoint> points, List<LongitudinalPoint> entered)
            {
                double first = points[0].Station, width = X(points.Last().Station - first);
                double table = 0.8 * TextHeight, mark = TextHeight, title = 1.2 * TextHeight;
                double left = -(DrainHeading + DrainGap) * Layout, separator = -DrainGap * Layout, indent = left + 1.35 * Layout;
                Func<int, double> x = index => X(points[index].Station - first);
                // A row with a surface elevation is a survey ordinate. Rows without
                // one carry only design data: a grade break, a new pipe diameter or
                // the inflow of another collector between ordinates.
                var survey = Enumerable.Range(0, points.Count).Where(index => entered[index].Ground.HasValue).ToList();
                var nodes = DrainNodes(points, entered);
                var pipes = DrainPipes(entered);
                var inflows = Enumerable.Range(0, points.Count).Where(index => !string.IsNullOrWhiteSpace(entered[index].PointName)).ToList();

                var ground = survey.Select(index => new Point2(x(index), Y(entered[index].Ground.Value))).ToList();
                DrainPeat(survey, entered, x);
                Curve(ground, "PROF_GROUND", 7, DrainLine);
                var drain = new List<Point2>();
                for (int index = 0; index < points.Count; index++)
                {
                    if (points[index].ProjectBottom.HasValue) { drain.Add(new Point2(x(index), Y(points[index].ProjectBottom.Value))); continue; }
                    Curve(drain, "PROF_DRAIN", 1, DrainWeight);
                    drain = new List<Point2>();
                }
                Curve(drain, "PROF_DRAIN", 1, DrainWeight);
                foreach (int index in new[] { 0, points.Count - 1 })
                {
                    double? top = points[index].Ground ?? points[index].ProjectBottom;
                    if (top.HasValue) Line(new Point2(x(index), 0), new Point2(x(index), Y(top.Value)), "PROF_GRID", 7, DrainLine);
                }

                // Heading column and table.
                var tops = new double[DrainRows.Length];
                var bottoms = new double[DrainRows.Length];
                double y = 0;
                for (int row = 0; row < DrainRows.Length; row++)
                {
                    IEnumerable<string> rotated = row == 2 ? survey.Select(index => F(entered[index].Ground.Value)) :
                        row == 4 ? nodes.Select(index => F(points[index].ProjectBottom.Value)) : Enumerable.Empty<string>();
                    double rotatedLength = rotated.Select(value => DrainLength(value, table) + 2 * Layout).DefaultIfEmpty(0).Max();
                    tops[row] = y;
                    y -= Math.Max(DrainRows[row] * Layout, rotatedLength);
                    bottoms[row] = y;
                }
                double bottom = y;
                Line(new Point2(left, 0), new Point2(width, 0), "PROF_GRID", 7, DrainLine);
                foreach (double rowBottom in bottoms) Line(new Point2(left, rowBottom), new Point2(width, rowBottom), "PROF_GRID", 7, DrainLine);
                Line(new Point2(left, 0), new Point2(left, bottom), "PROF_GRID", 7, DrainLine);
                Line(new Point2(separator, 0), new Point2(separator, bottom), "PROF_GRID", 7, DrainLine);
                Func<int, double> middle = row => (tops[row] + bottoms[row]) / 2;
                Text(indent, 1.17 * Layout + table / 2, "УСЛ. ГОР.", alignment: TextAlignment.Left, height: table);
                Text(indent, middle(0), "№ ОРДИНАТ", alignment: TextAlignment.Left, height: table);
                Text(indent, middle(1), "РАССТОЯНИЯ, М", alignment: TextAlignment.Left, height: table);
                Text(indent, tops[2] - 3.34 * Layout, "ОТМЕТКИ ПОВЕРХНОСТИ, М", alignment: TextAlignment.Left, height: table);
                Text(indent, middle(4), "ПРОЕКТНЫЕ ОТМЕТКИ, М", alignment: TextAlignment.Left, height: table);
                foreach (var heading in new[] { Tuple.Create(3, "УКЛОН", 2.52), Tuple.Create(5, "ДИАМЕТР", 2.36) })
                {
                    int row = heading.Item1;
                    Line(new Point2(left, bottoms[row]), new Point2(separator, tops[row]), "PROF_GRID", 7, DrainLine);
                    Text(indent, tops[row] - heading.Item3 * Layout, heading.Item2, alignment: TextAlignment.Left, height: table);
                    Text(separator - 1.27 * Layout, bottoms[row] + 2.66 * Layout, "ДЛИНА", alignment: TextAlignment.Right, height: table);
                }

                for (int ordinal = 0; ordinal < survey.Count; ordinal++)
                {
                    int index = survey[ordinal];
                    Text(x(index), tops[0] - 3.17 * Layout, ordinal.ToString(CultureInfo.InvariantCulture), height: table);
                    Text(x(index), middle(2), F(entered[index].Ground.Value), 90, height: table);
                    if (ordinal > 0)
                    {
                        int previous = survey[ordinal - 1];
                        Text((x(previous) + x(index)) / 2, middle(1), (points[index].Station - points[previous].Station).ToString("F1", CultureInfo.InvariantCulture), height: table);
                    }
                }
                CellBorders(survey.Select(x), width, tops[1], bottoms[1]);
                CellBorders(nodes.Select(x), width, tops[3], bottoms[3]);
                CellBorders(pipes.Select(pipe => x(pipe.Item1)), width, tops[5], bottoms[5]);
                for (int node = 0; node < nodes.Count; node++)
                {
                    int index = nodes[node];
                    Text(x(index), middle(4), F(points[index].ProjectBottom.Value), 90, height: table);
                    if (node == 0) continue;
                    int previous = nodes[node - 1];
                    double distance = points[index].Station - points[previous].Station;
                    double dz = points[index].ProjectBottom.Value - points[previous].ProjectBottom.Value;
                    DrainCell(x(previous), x(index), tops[3], bottoms[3], SlopeText(Math.Abs(dz) / distance), distance, Math.Sign(dz), table);
                }
                for (int pipe = 0; pipe < pipes.Count; pipe++)
                {
                    double start = x(pipes[pipe].Item1), end = pipe + 1 < pipes.Count ? x(pipes[pipe + 1].Item1) : width;
                    double startStation = points[pipes[pipe].Item1].Station;
                    double endStation = pipe + 1 < pipes.Count ? points[pipes[pipe + 1].Item1].Station : points.Last().Station;
                    if (end > start) DrainCell(start, end, tops[5], bottoms[5], pipes[pipe].Item2, endStation - startStation, 1, table);
                }

                // Marks above the table: conditional horizon, receiver bottom at the outlet,
                // inflowing collectors and the two-line title.
                double highest = new[] { 0.0 }.Concat(ground.Select(point => point.Y))
                    .Concat(points.Where(point => point.ProjectBottom.HasValue).Select(point => Y(point.ProjectBottom.Value))).Max();
                double datumTip = width >= 34 * Layout ? 20 * Layout : Math.Max(2.5 * Layout, width - 14 * Layout);
                ElevationMark(datumTip, 0, F(plan.Datum), false, mark);
                if (settings.OutletElevation.HasValue)
                {
                    double tip = -5.04 * Layout, level = Y(settings.OutletElevation.Value);
                    ElevationMark(tip, level, F(settings.OutletElevation.Value), true, mark);
                    Line(new Point2(tip - 1.65 * Layout, level), new Point2(0, level), "PROF_MARKS", 7, DrainLine);
                    highest = Math.Max(highest, level + 3.7 * Layout + 0.94 * Layout + mark);
                }
                double markerTop = Math.Max(highest + 11 * Layout, 15 * Layout);
                foreach (int index in inflows)
                {
                    Line(new Point2(x(index), 0), new Point2(x(index), markerTop), "PROF_MARKS", 7, DrainLine);
                    Text(x(index) - 0.66 * Layout - table / 2, markerTop - 2.4 * Layout, entered[index].PointName.Trim(), 90,
                        alignment: TextAlignment.Right, height: table);
                }
                string subtitle = (settings.Subtitle ?? "").Trim();
                double baseline = markerTop + 5.5 * Layout;
                if (subtitle.Length > 0)
                {
                    Text(2.04 * Layout, baseline + title / 2, subtitle, alignment: TextAlignment.Left, height: title);
                    baseline += 5 * Layout;
                }
                Text(2.04 * Layout, baseline + title / 2, (settings.Title ?? "").Trim(), alignment: TextAlignment.Left, height: title);

                foreach (var label in plan.Entities.OfType<DrawText>()) label.FontFile = DrainFont;
                if (survey.Count < 2)
                    plan.Warnings.Add("Для таблицы ординат нужны хотя бы две строки с отметкой поверхности.");
                if (nodes.Count == 0)
                    plan.Warnings.Add("Не задана проектная отметка дрены: линия дрены, уклоны и проектные отметки не построены.");
                if (survey.Zip(survey.Skip(1), (a, b) => x(b) - x(a) < 6 * Layout).Any(narrow => narrow))
                    plan.Warnings.Add("Есть расстояния между ординатами уже 6 мм на бумаге: подписи могут пересекаться. Уменьшите знаменатель горизонтального масштаба.");
            }

            /// <summary>Entered grade breaks, plus the extrapolated end of the drain line if it continues past the last one.</summary>
            private static List<int> DrainNodes(List<LongitudinalPoint> points, List<LongitudinalPoint> entered)
            {
                var nodes = Enumerable.Range(0, points.Count).Where(index => entered[index].ProjectBottom.HasValue).ToList();
                int end = points.Count - 1;
                if (nodes.Count > 0 && nodes.Last() < end && points[end].ProjectBottom.HasValue) nodes.Add(end);
                return nodes;
            }

            /// <summary>Rows that start a pipe of another diameter; a repeated diameter continues the same pipe.</summary>
            private static List<Tuple<int, string>> DrainPipes(List<LongitudinalPoint> entered)
            {
                var pipes = new List<Tuple<int, string>>();
                for (int index = 0; index < entered.Count; index++)
                {
                    string diameter = (entered[index].PipeDescription ?? "").Trim();
                    if (diameter.Length == 0 || (pipes.Count > 0 && pipes.Last().Item2 == diameter)) continue;
                    pipes.Add(Tuple.Create(index, diameter));
                }
                return pipes;
            }

            /// <summary>Advance of an ISOCPEUR string; widths per cap height are read from the font embedded in the reference PDF.</summary>
            private double DrainLength(string value, double height) => value.Sum(c => c == '1' ? 0.4933 : c == '4' ? 0.7893 : c == '.' ? 0.296 :
                char.IsDigit(c) || c == '-' || c == '+' ? 0.6907 : 0.9) * height * settings.TextWidthFactor;

            private static string SlopeText(double slope)
            {
                string text = slope.ToString("0.####", CultureInfo.InvariantCulture);
                return text == "0" && slope > 0 ? slope.ToString("0.0#######", CultureInfo.InvariantCulture) : text;
            }

            private void CellBorders(IEnumerable<double> xs, double width, double top, double bottom)
            {
                foreach (double position in xs.Concat(new[] { 0, width }).Distinct())
                    Line(new Point2(position, top), new Point2(position, bottom), "PROF_GRID", 7, DrainLine);
            }

            /// <summary>
            /// One «value / length» cell split by a diagonal, as for slopes and pipe
            /// diameters on the reference sheets: the value sits in the free top
            /// corner, the length in the opposite bottom corner.
            /// </summary>
            private void DrainCell(double start, double end, double top, double bottom, string value, double length, int direction, double height)
            {
                string metres = length.ToString("F1", CultureInfo.InvariantCulture);
                if (direction == 0)
                {
                    Line(new Point2(start, (top + bottom) / 2), new Point2(end, (top + bottom) / 2), "PROF_GRID", 7, DrainLine);
                    Text((start + end) / 2, top - 1.97 * Layout, value, height: height);
                    Text((start + end) / 2, bottom + 2.26 * Layout, metres, height: height);
                    return;
                }
                bool rising = direction > 0;
                Line(new Point2(start, rising ? bottom : top), new Point2(end, rising ? top : bottom), "PROF_GRID", 7, DrainLine);
                if (rising) Text(start + 1.05 * Layout, top - 1.97 * Layout, value, alignment: TextAlignment.Left, height: height);
                else Text(end - 1.05 * Layout, top - 1.97 * Layout, value, alignment: TextAlignment.Right, height: height);
                if (rising) Text(end - 1.86 * Layout, bottom + 2.26 * Layout, metres, alignment: TextAlignment.Right, height: height);
                else Text(start + 1.86 * Layout, bottom + 2.26 * Layout, metres, alignment: TextAlignment.Left, height: height);
            }

            /// <summary>Level mark: a 45° arrow touching the level, a 3.7 mm stem and a 12.5 mm shelf carrying the value.</summary>
            private void ElevationMark(double tipX, double tipY, string value, bool mirrored, double height)
            {
                double wing = 2.12 * Layout, stem = 3.7 * Layout, shelf = 12.47 * Layout, side = mirrored ? -1 : 1;
                Line(new Point2(tipX - wing, tipY + wing), new Point2(tipX, tipY), "PROF_MARKS", 7, DrainLine);
                Line(new Point2(tipX + wing, tipY + wing), new Point2(tipX, tipY), "PROF_MARKS", 7, DrainLine);
                Line(new Point2(tipX, tipY), new Point2(tipX, tipY + stem), "PROF_MARKS", 7, DrainLine);
                Line(new Point2(tipX, tipY + stem), new Point2(tipX + side * shelf, tipY + stem), "PROF_MARKS", 7, DrainLine);
                Text(tipX + side * 2.04 * Layout, tipY + stem + 0.94 * Layout + height / 2, value,
                    alignment: mirrored ? TextAlignment.Right : TextAlignment.Left, height: height);
            }

            /// <summary>Peat between the surface and its depth at survey ordinates: a cyan 2.5/1.0 mm dashed base and «V» marks.</summary>
            private void DrainPeat(List<int> survey, List<LongitudinalPoint> entered, Func<int, double> x)
            {
                var run = new List<int>();
                foreach (int index in survey.Concat(new[] { -1 }))
                {
                    if (index >= 0 && entered[index].PeatDepth.HasValue) { run.Add(index); continue; }
                    if (run.Count >= 2 && run.Any(item => entered[item].PeatDepth.Value > 0))
                    {
                        var top = run.Select(item => new Point2(x(item), Y(entered[item].Ground.Value))).ToList();
                        var baseLine = run.Select(item => new Point2(x(item), Y(entered[item].Ground.Value - entered[item].PeatDepth.Value))).ToList();
                        plan.Entities.Add(new DrawPolyline { Layer = "PROF_PEAT", ColorIndex = 4, LineWeightMm = DrainLine, Dashed = true, DashMm = 2.5, GapMm = 1.0, Points = baseLine });
                        var region = top.Concat(Enumerable.Reverse(baseLine)).ToList();
                        var marks = PeatPattern.Marks(region, Paper, 100000);
                        if (marks == null) plan.Warnings.Add("Слой торфа слишком велик для штриховки «галочками»: показана только его граница.");
                        else foreach (var stroke in marks)
                            plan.Entities.Add(new DrawPolyline { Layer = "PROF_PEAT", ColorIndex = 4, LineWeightMm = DrainLine, Points = stroke });
                    }
                    run = new List<int>();
                }
            }
        }
    }

    /// <summary>
    /// The peat hatch of the reference sheets: «V» marks of two 2 mm strokes at
    /// 45°, 5.66 mm apart along rows 2.83 mm apart, alternate rows shifted by
    /// half a step, clipped to the layer. Sizes are paper millimetres.
    /// </summary>
    public static class PeatPattern
    {
        public static List<List<Point2>> Marks(IList<Point2> polygon, double unit, int limit)
        {
            var result = new List<List<Point2>>();
            if (polygon == null || polygon.Count < 3 || !(unit > 0)) return result;
            double rowStep = 2 * Math.Sqrt(2) * unit, columnStep = 4 * Math.Sqrt(2) * unit, arm = Math.Sqrt(2) * unit;
            double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X), minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);
            for (long row = (long)Math.Floor((minY - arm) / rowStep); row * rowStep <= maxY; row++)
            {
                double y = row * rowStep, offset = (row % 2 == 0) ? 0 : columnStep / 2;
                for (long column = (long)Math.Floor((minX - arm - offset) / columnStep); column * columnStep + offset - arm <= maxX; column++)
                {
                    var apex = new Point2(column * columnStep + offset, y);
                    var leftEnd = new Point2(apex.X - arm, y + arm);
                    var rightEnd = new Point2(apex.X + arm, y + arm);
                    var leftParts = Inside(apex, leftEnd, polygon);
                    var rightParts = Inside(apex, rightEnd, polygon);
                    if (leftParts.Count == 1 && rightParts.Count == 1 && leftParts[0].Item1 <= 1e-9 && rightParts[0].Item1 <= 1e-9 &&
                        leftParts[0].Item2 >= 1 - 1e-9 && rightParts[0].Item2 >= 1 - 1e-9)
                        result.Add(new List<Point2> { leftEnd, apex, rightEnd });
                    else
                    {
                        foreach (var part in leftParts) result.Add(new List<Point2> { Along(apex, leftEnd, part.Item1), Along(apex, leftEnd, part.Item2) });
                        foreach (var part in rightParts) result.Add(new List<Point2> { Along(apex, rightEnd, part.Item1), Along(apex, rightEnd, part.Item2) });
                    }
                    if (result.Count > limit) return null;
                }
            }
            return result;
        }

        /// <summary>Parameter intervals of segment a–b lying inside the polygon (even–odd rule), shorter pieces dropped.</summary>
        public static List<Tuple<double, double>> Inside(Point2 a, Point2 b, IList<Point2> polygon)
        {
            var cuts = new List<double> { 0, 1 };
            for (int index = 0; index < polygon.Count; index++)
            {
                Point2 p = polygon[index], q = polygon[(index + 1) % polygon.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y, ex = q.X - p.X, ey = q.Y - p.Y;
                double denominator = dx * ey - dy * ex;
                if (Math.Abs(denominator) < 1e-15) continue;
                double t = ((p.X - a.X) * ey - (p.Y - a.Y) * ex) / denominator;
                double u = ((p.X - a.X) * dy - (p.Y - a.Y) * dx) / denominator;
                if (t > 0 && t < 1 && u >= 0 && u <= 1) cuts.Add(t);
            }
            cuts.Sort();
            var parts = new List<Tuple<double, double>>();
            for (int index = 1; index < cuts.Count; index++)
            {
                double from = cuts[index - 1], to = cuts[index];
                if (to - from < 1e-6 || !Contains(polygon, Along(a, b, (from + to) / 2))) continue;
                if (parts.Count > 0 && Math.Abs(parts[parts.Count - 1].Item2 - from) < 1e-9)
                    parts[parts.Count - 1] = Tuple.Create(parts[parts.Count - 1].Item1, to);
                else parts.Add(Tuple.Create(from, to));
            }
            // Pieces shorter than 5 % of a stroke only add specks along the boundary.
            return parts.Where(part => part.Item2 - part.Item1 >= 0.05).ToList();
        }

        private static Point2 Along(Point2 a, Point2 b, double t) => new Point2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        private static bool Contains(IList<Point2> polygon, Point2 point)
        {
            bool inside = false;
            for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
            {
                Point2 p = polygon[index], q = polygon[previous];
                if ((p.Y > point.Y) != (q.Y > point.Y) && point.X < (q.X - p.X) * (point.Y - p.Y) / (q.Y - p.Y) + p.X)
                    inside = !inside;
            }
            return inside;
        }
    }
}
