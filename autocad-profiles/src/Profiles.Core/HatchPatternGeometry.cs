using System;
using System.Collections.Generic;

namespace Profiles.Core
{
    public sealed class HatchCoverage
    {
        public double Spacing { get; internal set; }
        public bool DensityLimited { get; internal set; }
        /// <summary>Worst-phase distance to a stripe along nonparallel convex-corner edges.</summary>
        public double MaximumTipGap { get; internal set; }
    }

    public sealed class HatchStroke
    {
        public Point2 Start { get; internal set; }
        public Point2 End { get; internal set; }
    }

    /// <summary>
    /// A single parallel-line pattern anchored at the profile's local origin.
    /// Coverage changes its pitch, never the physical polygon or measured area.
    /// </summary>
    public static class HatchPatternGeometry
    {
        public const int MaximumPatternLines = 50000;
        private const double Epsilon = 2.2204460492503131e-16;

        public static HatchCoverage Coverage(IList<Point2> boundary, double angleDegrees,
            double preferredSpacing, double maximumTipGap)
        {
            if (!Finite(preferredSpacing) || preferredSpacing <= 0 || !Finite(maximumTipGap) || maximumTipGap <= 0)
                throw new ProfileValidationException("Некорректный шаг штриховки площади.");
            var points = CleanBoundary(boundary, true);
            Direction(angleDegrees, out double dx, out double dy);
            double nx = -dy, ny = dx;
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            var origin = points[0];
            foreach (var point in points)
            {
                double across = (point.X - origin.X) * nx + (point.Y - origin.Y) * ny;
                minimum = Math.Min(minimum, across); maximum = Math.Max(maximum, across);
            }
            double span = maximum - minimum;
            double spacing = Math.Min(preferredSpacing, span / 3);
            double area = TwiceArea(points), smallestIncidence = double.PositiveInfinity;
            if (area == 0 || !Finite(area) || span <= 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую площадь.");
            for (int i = 0; i < points.Count; i++)
            {
                var before = points[(i + points.Count - 1) % points.Count];
                var corner = points[i]; var after = points[(i + 1) % points.Count];
                double ax = corner.X - before.X, ay = corner.Y - before.Y;
                double bx = after.X - corner.X, by = after.Y - corner.Y;
                double cross = ax * by - ay * bx;
                double tolerance = 64 * Epsilon * (Math.Abs(ax * by) + Math.Abs(ay * bx));
                if (Math.Sign(area) * cross <= tolerance) continue; // Concave/collinear vertices are not tips.
                LimitEdge(ax, ay, nx, ny, maximumTipGap, ref spacing, ref smallestIncidence);
                LimitEdge(bx, by, nx, ny, maximumTipGap, ref spacing, ref smallestIncidence);
            }
            // Keep the native evaluator and preview bounded for extreme, valid
            // slivers. The caller reports when the requested tip bound is relaxed.
            double floor = span / (MaximumPatternLines - 2);
            bool limited = spacing < floor;
            spacing = Math.Max(spacing, floor);
            return new HatchCoverage
            {
                Spacing = spacing,
                DensityLimited = limited,
                MaximumTipGap = double.IsPositiveInfinity(smallestIncidence) ? spacing : spacing / smallestIncidence
            };
        }

        private static void LimitEdge(double x, double y, double nx, double ny, double maximumTipGap,
            ref double spacing, ref double smallestIncidence)
        {
            double length = Math.Sqrt(x * x + y * y);
            double projected = Math.Abs(x * nx + y * ny), incidence = projected / length;
            // A truly parallel boundary has no along-edge stripe crossing. Ignore
            // only floating rotation noise; the other incident edge still counts.
            if (incidence <= 64 * Epsilon) return;
            smallestIncidence = Math.Min(smallestIncidence, incidence);
            spacing = Math.Min(spacing, Math.Min(maximumTipGap * incidence, projected / 3));
        }

        /// <summary>
        /// Exact even/odd clipping of the same origin-zero lattice used by native
        /// HATCH. Concave notches split strokes; pan/zoom never change the phase.
        /// </summary>
        public static List<HatchStroke> Clip(IList<Point2> boundary, double angleDegrees, double spacing,
            int maximumStrokes = 500000)
        {
            if (!Finite(spacing) || spacing <= 0)
                throw new ProfileValidationException("Некорректный шаг штриховки площади.");
            if (maximumStrokes < 1 || maximumStrokes > 500000) throw new ArgumentOutOfRangeException(nameof(maximumStrokes));
            var points = CleanBoundary(boundary, false);
            Direction(angleDegrees, out double dx, out double dy);
            double nx = -dy, ny = dx;
            var rotated = new List<Point2>(points.Count);
            var origin = points[0];
            double originAcross = origin.X * nx + origin.Y * ny;
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            foreach (var point in points)
            {
                double x = point.X - origin.X, y = point.Y - origin.Y;
                double across = x * nx + y * ny;
                rotated.Add(new Point2(x * dx + y * dy, across));
                minimum = Math.Min(minimum, across); maximum = Math.Max(maximum, across);
            }
            if ((maximum - minimum) / spacing > MaximumPatternLines)
                throw new ProfileValidationException("Штриховка площади слишком плотная для отображения.");
            // Reduce only the phase modulo one period. Relative vertex positions
            // retain narrow layers even at large authored survey coordinates.
            double phase = Math.IEEERemainder(originAcross, spacing);
            double first = Math.Ceiling((minimum + phase) / spacing) * spacing - phase;
            int count = Math.Max(0, (int)Math.Floor((maximum - first) / spacing) + 1);
            if (!Finite(first) || count > MaximumPatternLines + 1)
                throw new ProfileValidationException("Некорректный диапазон штриховки площади.");
            var edges = new List<Edge>();
            for (int i = 0; i < rotated.Count; i++)
            {
                var a = rotated[i]; var b = rotated[(i + 1) % rotated.Count];
                if (a.Y == b.Y) continue;
                edges.Add(a.Y < b.Y ? new Edge(a, b) : new Edge(b, a));
            }
            edges.Sort((a, b) => a.Low.Y.CompareTo(b.Low.Y));
            var active = new List<Edge>(); var intersections = new List<double>();
            var strokes = new List<HatchStroke>(); int next = 0;
            for (int index = 0; index < count; index++)
            {
                double across = first + index * spacing;
                while (next < edges.Count && edges[next].Low.Y <= across) active.Add(edges[next++]);
                intersections.Clear();
                for (int j = active.Count - 1; j >= 0; j--)
                {
                    var edge = active[j];
                    if (edge.High.Y <= across) { active.RemoveAt(j); continue; }
                    double t = (across - edge.Low.Y) / (edge.High.Y - edge.Low.Y);
                    intersections.Add(edge.Low.X + t * (edge.High.X - edge.Low.X));
                }
                intersections.Sort();
                for (int j = 0; j + 1 < intersections.Count; j += 2)
                {
                    double start = intersections[j], end = intersections[j + 1];
                    if (end <= start) continue;
                    strokes.Add(new HatchStroke
                    {
                        Start = new Point2(origin.X + start * dx + across * nx, origin.Y + start * dy + across * ny),
                        End = new Point2(origin.X + end * dx + across * nx, origin.Y + end * dy + across * ny)
                    });
                    if (strokes.Count > maximumStrokes)
                        throw new ProfileValidationException("Слишком сложная штриховка площади для отображения.");
                }
            }
            return strokes;
        }

        private sealed class Edge
        {
            internal readonly Point2 Low, High;
            internal Edge(Point2 low, Point2 high) { Low = low; High = high; }
        }

        private static List<Point2> CleanBoundary(IList<Point2> boundary, bool simplify)
        {
            if (boundary == null || boundary.Count < 3 || boundary.Count > 250000)
                throw new ProfileValidationException("Некорректная граница штриховки площади.");
            var result = new List<Point2>(boundary.Count);
            foreach (var point in boundary)
            {
                if (!Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12)
                    throw new ProfileValidationException("Некорректные координаты границы штриховки площади.");
                if (result.Count > 0 && Same(result[result.Count - 1], point)) continue;
                while (simplify && result.Count > 1 && Collinear(result[result.Count - 2], result[result.Count - 1], point))
                    result.RemoveAt(result.Count - 1);
                result.Add(point);
            }
            if (result.Count > 1 && Same(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            while (simplify && result.Count > 3)
            {
                if (Collinear(result[result.Count - 2], result[result.Count - 1], result[0])) result.RemoveAt(result.Count - 1);
                else if (Collinear(result[result.Count - 1], result[0], result[1])) result.RemoveAt(0);
                else break;
            }
            if (simplify && (result.Count < 3 || TwiceArea(result) == 0))
                return CleanBoundary(boundary, false); // Preserve any genuinely nonzero, very thin polygon.
            if (result.Count < 3 || TwiceArea(result) == 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую площадь.");
            return result;
        }

        private static bool Collinear(Point2 a, Point2 b, Point2 c)
        {
            double ax = b.X - a.X, ay = b.Y - a.Y, bx = c.X - b.X, by = c.Y - b.Y;
            return ax * bx + ay * by >= 0 && Math.Abs(ax * by - ay * bx) <=
                64 * Epsilon * (Math.Abs(ax * by) + Math.Abs(ay * bx));
        }
        private static double TwiceArea(IList<Point2> points)
        {
            var origin = points[0]; double result = 0;
            for (int i = 1; i + 1 < points.Count; i++)
                result += (points[i].X - origin.X) * (points[i + 1].Y - origin.Y) -
                    (points[i + 1].X - origin.X) * (points[i].Y - origin.Y);
            return result;
        }
        private static void Direction(double degrees, out double x, out double y)
        {
            if (!Finite(degrees)) throw new ProfileValidationException("Некорректный угол штриховки площади.");
            double radians = (degrees % 360) * Math.PI / 180;
            x = Math.Cos(radians); y = Math.Sin(radians);
        }
        private static bool Same(Point2 a, Point2 b) => a.X == b.X && a.Y == b.Y;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
