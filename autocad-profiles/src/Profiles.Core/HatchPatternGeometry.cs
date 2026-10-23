using System;
using System.Collections.Generic;

namespace Profiles.Core
{
    public sealed class HatchStroke
    {
        public Point2 Start { get; internal set; }
        public Point2 End { get; internal set; }
    }

    /// <summary>
    /// A single parallel-line pattern anchored at the profile's local origin.
    /// Physical polygon boundaries are always complete; corners never densify the whole pattern.
    /// </summary>
    public static class HatchPatternGeometry
    {
        public const int MaximumPatternLines = 50000;

        /// <summary>Ordinary readable pitch; only an entire tiny island needs a smaller pitch.</summary>
        public static double NormalSpacing(IList<Point2> boundary, double angleDegrees, double preferredSpacing)
        {
            if (!Finite(preferredSpacing) || preferredSpacing <= 0)
                throw new ProfileValidationException("Некорректный шаг штриховки площади.");
            var points = CleanBoundary(boundary);
            Direction(angleDegrees, out double dx, out double dy);
            var origin = points[0];
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            foreach (var point in points)
            {
                double across = -(point.X - origin.X) * dy + (point.Y - origin.Y) * dx;
                minimum = Math.Min(minimum, across); maximum = Math.Max(maximum, across);
            }
            double span = maximum - minimum;
            if (!Finite(span) || span <= 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую площадь.");
            return Math.Max(span / (MaximumPatternLines - 2), Math.Min(preferredSpacing, span / 3));
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
            var points = CleanBoundary(boundary);
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

        private static List<Point2> CleanBoundary(IList<Point2> boundary)
        {
            if (boundary == null || boundary.Count < 3 || boundary.Count > 250000)
                throw new ProfileValidationException("Некорректная граница штриховки площади.");
            var result = new List<Point2>(boundary.Count);
            foreach (var point in boundary)
            {
                if (!Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12)
                    throw new ProfileValidationException("Некорректные координаты границы штриховки площади.");
                if (result.Count > 0 && Same(result[result.Count - 1], point)) continue;
                result.Add(point);
            }
            if (result.Count > 1 && Same(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            if (result.Count < 3 || TwiceArea(result) == 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую площадь.");
            return result;
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
