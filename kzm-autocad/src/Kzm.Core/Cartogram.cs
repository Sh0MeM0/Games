using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    public enum LabelScheme
    {
        Numbers = 0,
        Letters = 1,
    }

    /// <summary>Сетка квадратов: шаг, базовая точка (узел 0,0), угол поворота осей.</summary>
    public sealed class GridDefinition
    {
        public GridDefinition(double step, XY origin, double angle)
        {
            Guard.Positive(step, "Шаг сетки");
            if (!Guard.Finite(angle)) throw new KzmException("Угол поворота сетки не определён.");
            Step = step;
            Origin = origin;
            Angle = angle;
            cos = Math.Cos(angle);
            sin = Math.Sin(angle);
        }

        private readonly double cos, sin;
        public double Step { get; }
        public XY Origin { get; }
        /// <summary>Radians, counter-clockwise from the X axis.</summary>
        public double Angle { get; }

        public XY ToWorld(double u, double v) => new XY(Origin.X + u * cos - v * sin, Origin.Y + u * sin + v * cos);

        public (double U, double V) ToLocal(XY p)
        {
            double dx = p.X - Origin.X, dy = p.Y - Origin.Y;
            return (dx * cos + dy * sin, -dx * sin + dy * cos);
        }

        public XY Node(int i, int j) => ToWorld(i * Step, j * Step);
    }

    /// <summary>Обозначение рядов и столбцов.</summary>
    public static class GridLabels
    {
        /// <summary>Letters as used for building axes: without Ё, З, Й, О, Х, Ц, Ч, Щ, Ъ, Ы, Ь.</summary>
        private const string Alphabet = "АБВГДЕЖИКЛМНПРСТУФШЭЮЯ";

        public static string Label(LabelScheme scheme, int ordinal)
        {
            if (ordinal < 1) throw new ArgumentOutOfRangeException(nameof(ordinal));
            if (scheme == LabelScheme.Numbers) return ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string text = string.Empty;
            int n = ordinal;
            while (n > 0)
            {
                n--;
                text = Alphabet[n % Alphabet.Length] + text;
                n /= Alphabet.Length;
            }
            return text;
        }
    }

    public sealed class CartogramNode
    {
        public int I { get; internal set; }
        public int J { get; internal set; }
        public XY Position { get; internal set; }
        public double? Fact { get; internal set; }
        public double? Project { get; internal set; }
        public double? Working => Fact.HasValue && Project.HasValue ? Project - Fact : null;
        /// <summary>The node is a corner of at least one square that has area inside the boundary.</summary>
        public bool Used { get; internal set; }
        /// <summary>Inside the boundary or on it: labelled on the drawing (nodes outside are not drawn).</summary>
        public bool Inside { get; internal set; }
    }

    /// <summary>
    /// Точка пересечения линии сетки с границей работ. В ней, как в узле, подписываются
    /// фактическая, проектная и рабочая отметки (интерполяция по поверхностям).
    /// </summary>
    public sealed class BoundaryPoint
    {
        public XY Position { get; internal set; }
        public double? Fact { get; internal set; }
        public double? Project { get; internal set; }
        public double? Working => Fact.HasValue && Project.HasValue ? Project - Fact : null;
        /// <summary>Stable key (grid axes, mm): the same point keeps its key after a rebuild.</summary>
        public string Key { get; internal set; }
    }

    public sealed class CartogramSquare
    {
        public int I { get; internal set; }
        public int J { get; internal set; }
        /// <summary>Fill (насыпь, +), m³.</summary>
        public double Fill { get; internal set; }
        /// <summary>Cut (выемка, −) as a positive number, m³.</summary>
        public double Cut { get; internal set; }
        /// <summary>Area inside the boundary, m².</summary>
        public double Area { get; internal set; }
        public bool Full { get; internal set; }
        public XY LabelPoint { get; internal set; }
        public string Problem { get; internal set; }
        public bool Computed => Problem == null;
        public List<XY[]> CutPolygons { get; } = new List<XY[]>();
        public List<(XY A, XY B)> ZeroSegments { get; } = new List<(XY, XY)>();
    }

    public sealed class CartogramResult
    {
        internal CartogramResult(GridDefinition grid) { Grid = grid; }

        public GridDefinition Grid { get; }
        public int IMin { get; internal set; }
        public int IMax { get; internal set; }
        public int JMin { get; internal set; }
        public int JMax { get; internal set; }
        public Dictionary<(int, int), CartogramNode> Nodes { get; } = new Dictionary<(int, int), CartogramNode>();
        public List<CartogramSquare> Squares { get; } = new List<CartogramSquare>();
        public Dictionary<int, (double Fill, double Cut)> ColumnTotals { get; } = new Dictionary<int, (double, double)>();
        public Dictionary<int, (double Fill, double Cut)> RowTotals { get; } = new Dictionary<int, (double, double)>();
        public double TotalFill { get; internal set; }
        public double TotalCut { get; internal set; }
        public double BoundaryArea { get; internal set; }
        public double ComputedArea { get; internal set; }
        public List<List<XY>> ZeroLines { get; } = new List<List<XY>>();
        /// <summary>Grid lines clipped by the boundary (drawing coordinates).</summary>
        public List<(XY A, XY B)> GridSegments { get; } = new List<(XY, XY)>();
        /// <summary>Crossings of the grid lines with the boundary.</summary>
        public List<BoundaryPoint> BoundaryPoints { get; } = new List<BoundaryPoint>();
        public List<string> Problems { get; } = new List<string>();
        /// <summary>Сумма по столбцам = сумма по рядам = общий итог.</summary>
        public bool Balanced { get; internal set; }
        /// <summary>Squares that needed the ear-clipping fallback.</summary>
        public int ComplexSquares { get; internal set; }

        public int Columns => IMax - IMin + 1;
        public int Rows => JMax - JMin + 1;
    }

    /// <summary>
    /// Метод квадратов. Квадрат делится на 4 треугольника через центр, h0 = (h1+h2+h3+h4)/4.
    /// Треугольник обрезается контуром; отметки в новых точках (на контуре) берутся
    /// интерполяцией по поверхностям (TIN). Треугольник со сменой знака делится точками
    /// нулевых работ (линейная интерполяция по ребру); каждая часть: V = S·(h1+h2+h3)/3.
    /// </summary>
    public static class CartogramCalculator
    {
        private const double Tiny = 1e-9;

        public static CartogramResult Compute(IReadOnlyList<XY> boundary, Func<XY, double?> fact, Func<XY, double?> project, GridDefinition grid)
        {
            if (boundary == null || boundary.Count < 3) throw new KzmException("Граница картограммы не задана (нужна замкнутая полилиния).");
            if (fact == null || project == null) throw new ArgumentNullException(fact == null ? nameof(fact) : nameof(project));
            var outline = Geometry.Normalize(boundary);
            if (outline.Count < 3) throw new KzmException("Граница картограммы вырождена (нулевая площадь).");
            if (Geometry.IsSelfIntersecting(outline)) throw new KzmException("Граница картограммы самопересекается.");
            var result = new CartogramResult(grid);

            // The calculation runs in the axes of the grid (node 0,0 at the origin): squares are exact
            // there and areas keep their precision with survey coordinates (~5 000 000 m).
            var local = Geometry.Normalize(outline.Select(p => ToLocal(grid, p)).ToList());
            result.BoundaryArea = Geometry.Area(local);
            double step = grid.Step;
            (result.IMin, result.IMax, result.JMin, result.JMax) = Range(local, step);
            if ((long)(result.IMax - result.IMin + 1) * (result.JMax - result.JMin + 1) > 1_000_000)
                throw new KzmException("Слишком много квадратов (больше 1 000 000): проверьте шаг сетки.");

            CartogramNode Node(int i, int j)
            {
                if (result.Nodes.TryGetValue((i, j), out var node)) return node;
                var position = grid.Node(i, j);
                node = new CartogramNode { I = i, J = j, Position = position, Fact = fact(position), Project = project(position) };
                result.Nodes[(i, j)] = node;
                return node;
            }

            var zeroSegments = new List<(XY, XY)>();
            for (int i = result.IMin; i <= result.IMax; i++)
            for (int j = result.JMin; j <= result.JMax; j++)
            {
                var corners = new[] { new XY(i * step, j * step), new XY((i + 1) * step, j * step), new XY((i + 1) * step, (j + 1) * step), new XY(i * step, (j + 1) * step) };
                var inside = Geometry.ClipByConvex(local, corners);
                double area = inside.Count >= 3 ? Geometry.Area(inside) : 0;
                if (area <= Tiny * step * step) continue;
                var center = Geometry.Centroid(inside);
                var square = new CartogramSquare
                {
                    I = i, J = j, Area = area,
                    Full = Math.Abs(area - step * step) <= 1e-9 * step * step,
                    LabelPoint = grid.ToWorld(center.X, center.Y),
                };
                result.Squares.Add(square);
                var nodes = new[] { Node(i, j), Node(i + 1, j), Node(i + 1, j + 1), Node(i, j + 1) };
                foreach (var node in nodes) node.Used = true;
                ComputeSquare(square, corners, nodes, local, fact, project, grid, result);
                // Results back to the drawing (world) coordinates.
                for (int k = 0; k < square.CutPolygons.Count; k++)
                    square.CutPolygons[k] = square.CutPolygons[k].Select(p => grid.ToWorld(p.X, p.Y)).ToArray();
                for (int k = 0; k < square.ZeroSegments.Count; k++)
                {
                    var (a, b) = square.ZeroSegments[k];
                    square.ZeroSegments[k] = (grid.ToWorld(a.X, a.Y), grid.ToWorld(b.X, b.Y));
                }
                zeroSegments.AddRange(square.ZeroSegments);
            }

            foreach (var square in result.Squares.Where(s => s.Computed))
            {
                result.ComputedArea += square.Area;
                result.ColumnTotals[square.I] = Add(result.ColumnTotals, square.I, square.Fill, square.Cut);
                result.RowTotals[square.J] = Add(result.RowTotals, square.J, square.Fill, square.Cut);
                result.TotalFill += square.Fill;
                result.TotalCut += square.Cut;
            }
            double columnsFill = result.ColumnTotals.Values.Sum(v => v.Fill), columnsCut = result.ColumnTotals.Values.Sum(v => v.Cut);
            double rowsFill = result.RowTotals.Values.Sum(v => v.Fill), rowsCut = result.RowTotals.Values.Sum(v => v.Cut);
            double tolerance = 1e-6 * Math.Max(1, result.TotalFill + result.TotalCut);
            result.Balanced = Math.Abs(columnsFill - result.TotalFill) <= tolerance && Math.Abs(rowsFill - result.TotalFill) <= tolerance &&
                              Math.Abs(columnsCut - result.TotalCut) <= tolerance && Math.Abs(rowsCut - result.TotalCut) <= tolerance;
            result.ZeroLines.AddRange(Chain(zeroSegments, step * 1e-7));
            int missing = result.Squares.Count(s => !s.Computed);
            if (missing > 0)
                result.Problems.Add("не рассчитано квадратов: " + missing + " — нет данных поверхности в нужных точках (см. пометки)");
            foreach (var node in result.Nodes.Values)
                node.Inside = Geometry.Contains(local, ToLocal(grid, node.Position), 1e-6 * step);
            AddGridSegments(result, local, grid);
            AddBoundaryPoints(result, local, grid, fact, project);
            int empty = result.Nodes.Values.Count(n => n.Used && n.Inside && !n.Working.HasValue) +
                        result.BoundaryPoints.Count(b => !b.Working.HasValue);
            if (empty > 0)
                result.Problems.Add("точек сетки и границы без данных поверхности: " + empty + " (помечены «нет данных»)");
            return result;
        }

        /// <summary>Lines of the grid inside the boundary (задание: сетка не выходит за границу работ).</summary>
        private static void AddGridSegments(CartogramResult result, List<XY> local, GridDefinition grid) =>
            result.GridSegments.AddRange(Segments(local, grid, result.IMin, result.IMax, result.JMin, result.JMax));

        /// <summary>The grid as it will be drawn (only inside the boundary): for a preview before building.</summary>
        public static List<(XY A, XY B)> GridSegments(IReadOnlyList<XY> boundary, GridDefinition grid)
        {
            var local = Geometry.Normalize(boundary.Select(p => ToLocal(grid, p)).ToList());
            if (local.Count < 3) return new List<(XY, XY)>();
            var (iMin, iMax, jMin, jMax) = Range(local, grid.Step);
            return Segments(local, grid, iMin, iMax, jMin, jMax);
        }

        private static List<(XY A, XY B)> Segments(List<XY> local, GridDefinition grid, int iMin, int iMax, int jMin, int jMax)
        {
            var segments = new List<(XY, XY)>();
            double s = grid.Step;
            double v0 = jMin * s, v1 = (jMax + 1) * s, u0 = iMin * s, u1 = (iMax + 1) * s;
            void Add(XY a, XY b)
            {
                foreach (var (p, q) in Geometry.ClipSegment(a, b, local))
                    if (p.DistanceTo(q) > 1e-9 * s) segments.Add((grid.ToWorld(p.X, p.Y), grid.ToWorld(q.X, q.Y)));
            }
            for (int i = iMin; i <= iMax + 1; i++) Add(new XY(i * s, v0 - s), new XY(i * s, v1 + s));
            for (int j = jMin; j <= jMax + 1; j++) Add(new XY(u0 - s, j * s), new XY(u1 + s, j * s));
            return segments;
        }

        /// <summary>Crossings of the grid lines with the boundary; crossings at nodes are nodes (not repeated).</summary>
        private static void AddBoundaryPoints(CartogramResult result, List<XY> local, GridDefinition grid, Func<XY, double?> fact, Func<XY, double?> project)
        {
            double s = grid.Step;
            double tolerance = 1e-6 * s;
            var seen = new HashSet<(long, long)>();
            void Add(XY p)
            {
                // A crossing that falls on a node is that node.
                double ri = p.X / s, rj = p.Y / s;
                if (Math.Abs(ri - Math.Round(ri)) * s <= tolerance && Math.Abs(rj - Math.Round(rj)) * s <= tolerance) return;
                var key = ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
                if (!seen.Add(key)) return;
                var world = grid.ToWorld(p.X, p.Y);
                result.BoundaryPoints.Add(new BoundaryPoint
                {
                    Position = world,
                    Fact = fact(world),
                    Project = project(world),
                    Key = "B:" + key.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + key.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            for (int k = 0, n = local.Count; k < n; k++)
            {
                var a = local[k];
                var b = local[(k + 1) % n];
                // Vertical lines u = i·s.
                int i0 = (int)Math.Ceiling(Math.Min(a.X, b.X) / s - 1e-9), i1 = (int)Math.Floor(Math.Max(a.X, b.X) / s + 1e-9);
                for (int i = i0; i <= i1; i++)
                {
                    double u = i * s;
                    if (Math.Abs(b.X - a.X) <= tolerance)
                    {
                        // The edge lies on the grid line: its ends are the crossings.
                        if (Math.Abs(a.X - u) <= tolerance) { Add(new XY(u, a.Y)); Add(new XY(u, b.Y)); }
                        continue;
                    }
                    double t = (u - a.X) / (b.X - a.X);
                    if (t < -1e-12 || t > 1 + 1e-12) continue;
                    Add(new XY(u, a.Y + t * (b.Y - a.Y)));
                }
                // Horizontal lines v = j·s.
                int j0 = (int)Math.Ceiling(Math.Min(a.Y, b.Y) / s - 1e-9), j1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / s + 1e-9);
                for (int j = j0; j <= j1; j++)
                {
                    double v = j * s;
                    if (Math.Abs(b.Y - a.Y) <= tolerance)
                    {
                        if (Math.Abs(a.Y - v) <= tolerance) { Add(new XY(a.X, v)); Add(new XY(b.X, v)); }
                        continue;
                    }
                    double t = (v - a.Y) / (b.Y - a.Y);
                    if (t < -1e-12 || t > 1 + 1e-12) continue;
                    Add(new XY(a.X + t * (b.X - a.X), v));
                }
            }
        }

        /// <summary>Squares (by index) that cover the outline given in grid axes.</summary>
        private static (int IMin, int IMax, int JMin, int JMax) Range(IReadOnlyList<XY> local, double step) =>
            ((int)Math.Floor(local.Min(p => p.X) / step + Tiny), (int)Math.Ceiling(local.Max(p => p.X) / step - Tiny) - 1,
             (int)Math.Floor(local.Min(p => p.Y) / step + Tiny), (int)Math.Ceiling(local.Max(p => p.Y) / step - Tiny) - 1);

        /// <summary>Squares of the grid that the boundary (drawing coordinates) touches — for a preview of the grid.</summary>
        public static (int IMin, int IMax, int JMin, int JMax) Range(IReadOnlyList<XY> boundary, GridDefinition grid) =>
            Range(boundary.Select(p => ToLocal(grid, p)).ToList(), grid.Step);

        private static XY ToLocal(GridDefinition grid, XY p)
        {
            var (u, v) = grid.ToLocal(p);
            return new XY(u, v);
        }

        private static (double, double) Add(Dictionary<int, (double Fill, double Cut)> totals, int key, double fill, double cut)
        {
            totals.TryGetValue(key, out var value);
            return (value.Fill + fill, value.Cut + cut);
        }

        /// <summary>One square in grid axes: <paramref name="corners"/> and <paramref name="outline"/> are local; heights are read in the drawing.</summary>
        private static void ComputeSquare(CartogramSquare square, XY[] corners, CartogramNode[] nodes, List<XY> outline,
            Func<XY, double?> fact, Func<XY, double?> project, GridDefinition grid, CartogramResult result)
        {
            var center = Geometry.Lerp(corners[0], corners[2], 0.5);
            double? h0;
            if (nodes.All(node => node.Working.HasValue)) h0 = nodes.Average(node => node.Working.Value);
            else h0 = Working(grid.ToWorld(center.X, center.Y), fact, project);

            var known = new List<(XY Point, double? H)>
            {
                (corners[0], nodes[0].Working), (corners[1], nodes[1].Working), (corners[2], nodes[2].Working), (corners[3], nodes[3].Working),
                (center, h0),
            };
            double tolerance = grid.Step * 1e-9;
            double? HeightAt(XY p)
            {
                foreach (var k in known) if (k.Point.DistanceTo(p) <= tolerance) return k.H;
                return Working(grid.ToWorld(p.X, p.Y), fact, project);
            }

            for (int k = 0; k < 4; k++)
            {
                var triangle = new[] { corners[k], corners[(k + 1) % 4], center };
                var clipped = Geometry.ClipByConvex(outline, triangle);
                if (clipped.Count < 3) continue;
                double area = Geometry.Area(clipped);
                double full = Geometry.Area(triangle);
                if (area <= Tiny * full) continue;
                List<XY[]> pieces;
                if (Math.Abs(area - full) <= 1e-9 * full) pieces = new List<XY[]> { triangle };
                else
                {
                    pieces = Geometry.Triangulate(Geometry.Normalize(clipped));
                    if (pieces == null || Math.Abs(pieces.Sum(p => Geometry.Area(p)) - area) > 1e-6 * full)
                    {
                        // The boundary enters this triangle several times: keep the exact area and use
                        // the plane of the grid triangle for the parts (linear interpolation, п. 5).
                        result.ComplexSquares++;
                        if (!PlanarFallback(square, triangle, clipped, HeightAt)) return;
                        continue;
                    }
                }
                foreach (var piece in pieces)
                {
                    var h = new double[3];
                    for (int v = 0; v < 3; v++)
                    {
                        var value = HeightAt(piece[v]);
                        if (!value.HasValue)
                        {
                            Fail(square, grid.ToWorld(piece[v].X, piece[v].Y));
                            return;
                        }
                        h[v] = value.Value;
                    }
                    Split(piece, h, square);
                }
            }
        }

        private static double? Working(XY p, Func<XY, double?> fact, Func<XY, double?> project)
        {
            var f = fact(p);
            var pr = project(p);
            return f.HasValue && pr.HasValue ? pr - f : null;
        }

        private static void Fail(CartogramSquare square, XY point)
        {
            square.Problem = "нет данных поверхности в точке " + ElevationFormat.Format(point.X, 2) + "; " + ElevationFormat.Format(point.Y, 2);
            square.Fill = square.Cut = 0;
            square.CutPolygons.Clear();
            square.ZeroSegments.Clear();
        }

        /// <summary>Fallback for a triangle crossed by the boundary several times (exact for a plane over the triangle).</summary>
        private static bool PlanarFallback(CartogramSquare square, XY[] triangle, List<XY> clipped, Func<XY, double?> heightAt)
        {
            var h = new double[3];
            for (int v = 0; v < 3; v++)
            {
                var value = heightAt(triangle[v]);
                if (!value.HasValue)
                {
                    square.Problem = "нет данных поверхности в вершине квадрата";
                    return false;
                }
                h[v] = value.Value;
            }
            // Positive and negative parts of the triangle are convex; clip the boundary by each.
            var (positive, negative, zero) = Parts(triangle, h);
            foreach (var (part, sign) in new[] { (positive, 1), (negative, -1) })
            {
                if (part.Count < 3) continue;
                var inside = Geometry.ClipByConvex(clipped, Geometry.Normalize(part));
                if (inside.Count < 3) continue;
                double area = Geometry.Area(inside);
                double value = area * Plane(triangle, h, Geometry.Centroid(inside));
                if (sign > 0) square.Fill += Math.Max(0, value);
                else
                {
                    square.Cut += Math.Max(0, -value);
                    square.CutPolygons.Add(inside.ToArray());
                }
            }
            if (zero.HasValue)
                foreach (var segment in Geometry.ClipSegment(zero.Value.A, zero.Value.B, clipped))
                    square.ZeroSegments.Add(segment);
            return true;
        }

        private static double Plane(XY[] t, double[] h, XY p)
        {
            double area = Geometry.Orient(t[0], t[1], t[2]);
            double wa = Geometry.Orient(t[1], t[2], p) / area;
            double wb = Geometry.Orient(t[2], t[0], p) / area;
            return wa * h[0] + wb * h[1] + (1 - wa - wb) * h[2];
        }

        private static int Sign(double h) => h > Tiny ? 1 : h < -Tiny ? -1 : 0;

        /// <summary>Positive and negative parts of a triangle (zero points by linear interpolation) and its zero segment.</summary>
        private static (List<XY> Positive, List<XY> Negative, (XY A, XY B)? Zero) Parts(XY[] p, double[] h)
        {
            var positive = new List<XY>();
            var negative = new List<XY>();
            var zeros = new List<XY>();
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                int si = Sign(h[i]), sj = Sign(h[j]);
                if (si >= 0) positive.Add(p[i]);
                if (si <= 0) negative.Add(p[i]);
                if (si == 0) zeros.Add(p[i]);
                if (si * sj < 0)
                {
                    var z = Geometry.Lerp(p[i], p[j], h[i] / (h[i] - h[j]));
                    positive.Add(z);
                    negative.Add(z);
                    zeros.Add(z);
                }
            }
            (XY, XY)? zero = null;
            bool mixed = h.Any(v => Sign(v) > 0) && h.Any(v => Sign(v) < 0);
            if (zeros.Count == 2 && (mixed || zeros[0].DistanceTo(zeros[1]) > 0)) zero = (zeros[0], zeros[1]);
            if (!h.Any(v => Sign(v) > 0)) positive.Clear();
            if (!h.Any(v => Sign(v) < 0)) negative.Clear();
            return (positive, negative, zero);
        }

        /// <summary>Volumes of one triangle with known h at its vertices (formula of п. 5).</summary>
        private static void Split(XY[] p, double[] h, CartogramSquare square)
        {
            var (positive, negative, zero) = Parts(p, h);
            double Value(List<XY> polygon)
            {
                double sum = 0;
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    var t = new[] { polygon[0], polygon[k], polygon[k + 1] };
                    sum += Geometry.Area(t) * (HeightOf(t[0]) + HeightOf(t[1]) + HeightOf(t[2])) / 3;
                }
                return sum;
            }
            double HeightOf(XY q)
            {
                for (int v = 0; v < 3; v++) if (p[v].Equals(q)) return h[v];
                return 0; // a zero point
            }
            if (positive.Count >= 3) square.Fill += Math.Max(0, Value(positive));
            if (negative.Count >= 3)
            {
                square.Cut += Math.Max(0, -Value(negative));
                square.CutPolygons.Add(negative.ToArray());
            }
            if (zero.HasValue) square.ZeroSegments.Add(zero.Value);
        }

        /// <summary>Joins segments with common ends into polylines.</summary>
        internal static List<List<XY>> Chain(List<(XY A, XY B)> segments, double tolerance)
        {
            (long, long) Key(XY p) => ((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance));
            var unique = new List<(XY A, XY B)>();
            var seen = new HashSet<((long, long), (long, long))>();
            foreach (var s in segments)
            {
                var a = Key(s.A);
                var b = Key(s.B);
                if (a.Equals(b)) continue;
                var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                if (seen.Add(key)) unique.Add(s);
            }
            var byEnd = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < unique.Count; i++)
                foreach (var end in new[] { Key(unique[i].A), Key(unique[i].B) })
                {
                    if (!byEnd.TryGetValue(end, out var list)) byEnd[end] = list = new List<int>();
                    list.Add(i);
                }
            var used = new bool[unique.Count];
            var lines = new List<List<XY>>();
            // Start from ends with one segment so that open lines are not split.
            var starts = Enumerable.Range(0, unique.Count)
                .OrderBy(i => byEnd[Key(unique[i].A)].Count == 1 || byEnd[Key(unique[i].B)].Count == 1 ? 0 : 1).ToList();
            foreach (int first in starts)
            {
                if (used[first]) continue;
                used[first] = true;
                var line = new LinkedList<XY>(new[] { unique[first].A, unique[first].B });
                if (byEnd[Key(unique[first].B)].Count == 1 && byEnd[Key(unique[first].A)].Count != 1)
                    line = new LinkedList<XY>(new[] { unique[first].B, unique[first].A });
                for (bool forward = true; ; forward = false)
                {
                    while (true)
                    {
                        var end = forward ? line.Last.Value : line.First.Value;
                        int found = -1;
                        foreach (int candidate in byEnd[Key(end)])
                            if (!used[candidate]) { found = candidate; break; }
                        if (found < 0) break;
                        used[found] = true;
                        var s = unique[found];
                        var other = Key(s.A).Equals(Key(end)) ? s.B : s.A;
                        if (forward) line.AddLast(other); else line.AddFirst(other);
                    }
                    if (!forward) break;
                }
                lines.Add(line.ToList());
            }
            return lines;
        }
    }
}
