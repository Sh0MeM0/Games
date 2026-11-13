using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>Plane geometry used by the triangulation and the cartogram.</summary>
    public static class Geometry
    {
        public const double Epsilon = 1e-9;

        /// <summary>Twice the signed area of triangle abc (&gt; 0 — counter-clockwise).</summary>
        public static double Orient(XY a, XY b, XY c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        public static double SignedArea(IReadOnlyList<XY> polygon)
        {
            double sum = 0;
            for (int i = 0, n = polygon.Count; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }

        public static double Area(IReadOnlyList<XY> polygon) => Math.Abs(SignedArea(polygon));

        public static XY Centroid(IReadOnlyList<XY> polygon)
        {
            double area = SignedArea(polygon);
            if (Math.Abs(area) < Epsilon)
                return new XY(polygon.Average(p => p.X), polygon.Average(p => p.Y));
            double cx = 0, cy = 0;
            for (int i = 0, n = polygon.Count; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                double cross = a.X * b.Y - b.X * a.Y;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            return new XY(cx / (6 * area), cy / (6 * area));
        }

        /// <summary>Even-odd test; points on the outline count as inside within the tolerance.</summary>
        public static bool Contains(IReadOnlyList<XY> polygon, XY point, double tolerance = 1e-7)
        {
            bool inside = false;
            for (int i = 0, n = polygon.Count, j = n - 1; i < n; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if (DistanceToSegment(point, a, b) <= tolerance) return true;
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        public static double DistanceToSegment(XY p, XY a, XY b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = dx * dx + dy * dy;
            if (length <= 0) return p.DistanceTo(a);
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length));
            return p.DistanceTo(new XY(a.X + t * dx, a.Y + t * dy));
        }

        /// <summary>Counter-clockwise copy of the polygon without repeated or collinear vertices.</summary>
        public static List<XY> Normalize(IEnumerable<XY> points, double tolerance = 1e-9)
        {
            var list = new List<XY>();
            foreach (var p in points)
                if (list.Count == 0 || list[list.Count - 1].DistanceTo(p) > tolerance) list.Add(p);
            while (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tolerance) list.RemoveAt(list.Count - 1);
            bool changed = true;
            while (changed && list.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < list.Count && list.Count >= 3; i++)
                {
                    var a = list[(i + list.Count - 1) % list.Count];
                    var b = list[i];
                    var c = list[(i + 1) % list.Count];
                    double scale = Math.Max(a.DistanceTo(b), b.DistanceTo(c));
                    if (Math.Abs(Orient(a, b, c)) <= tolerance * Math.Max(scale, 1))
                    {
                        list.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }
            if (list.Count >= 3 && SignedArea(list) < 0) list.Reverse();
            return list;
        }

        /// <summary>
        /// Sutherland–Hodgman: the part of <paramref name="subject"/> inside the convex,
        /// counter-clockwise <paramref name="window"/>.
        /// </summary>
        public static List<XY> ClipByConvex(IReadOnlyList<XY> subject, IReadOnlyList<XY> window)
        {
            var output = subject.ToList();
            for (int e = 0; e < window.Count && output.Count > 0; e++)
            {
                var a = window[e];
                var b = window[(e + 1) % window.Count];
                var input = output;
                output = new List<XY>(input.Count + 4);
                for (int i = 0; i < input.Count; i++)
                {
                    var current = input[i];
                    var previous = input[(i + input.Count - 1) % input.Count];
                    double sc = Orient(a, b, current), sp = Orient(a, b, previous);
                    bool cin = sc >= -Epsilon, pin = sp >= -Epsilon;
                    if (cin)
                    {
                        if (!pin) output.Add(Intersect(previous, current, sp, sc));
                        output.Add(current);
                    }
                    else if (pin)
                    {
                        output.Add(Intersect(previous, current, sp, sc));
                    }
                }
            }
            return output;
        }

        private static XY Intersect(XY p, XY q, double sp, double sq)
        {
            double t = sp / (sp - sq);
            return new XY(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t);
        }

        /// <summary>
        /// Ear clipping of a simple counter-clockwise polygon. Returns null when the polygon is
        /// not simple (for example several pieces joined by zero-width bridges).
        /// </summary>
        public static List<XY[]> Triangulate(IReadOnlyList<XY> polygon)
        {
            var points = polygon.ToList();
            var result = new List<XY[]>();
            if (points.Count < 3) return result;
            if (points.Count == 3)
            {
                result.Add(points.ToArray());
                return result;
            }
            int guard = points.Count * points.Count + 10;
            while (points.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[(i + points.Count - 1) % points.Count];
                    var b = points[i];
                    var c = points[(i + 1) % points.Count];
                    if (Orient(a, b, c) <= Epsilon) continue;
                    bool empty = true;
                    for (int k = 0; k < points.Count && empty; k++)
                    {
                        var p = points[k];
                        if (p.Equals(a) || p.Equals(b) || p.Equals(c)) continue;
                        if (Orient(a, b, p) >= -Epsilon && Orient(b, c, p) >= -Epsilon && Orient(c, a, p) >= -Epsilon) empty = false;
                    }
                    if (!empty) continue;
                    result.Add(new[] { a, b, c });
                    points.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) return null;
            }
            if (points.Count == 3 && Orient(points[0], points[1], points[2]) > Epsilon) result.Add(points.ToArray());
            return result;
        }

        /// <summary>Parts of segment ab that lie inside the polygon (a crossing-number test of each piece).</summary>
        public static List<(XY A, XY B)> ClipSegment(XY a, XY b, IReadOnlyList<XY> polygon)
        {
            var ts = new List<double> { 0, 1 };
            for (int i = 0, n = polygon.Count; i < n; i++)
            {
                var c = polygon[i];
                var d = polygon[(i + 1) % n];
                double denominator = (b.X - a.X) * (d.Y - c.Y) - (b.Y - a.Y) * (d.X - c.X);
                if (Math.Abs(denominator) < 1e-15) continue;
                double t = ((c.X - a.X) * (d.Y - c.Y) - (c.Y - a.Y) * (d.X - c.X)) / denominator;
                double u = ((c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X)) / denominator;
                if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
            }
            ts.Sort();
            var result = new List<(XY, XY)>();
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                if (ts[i + 1] - ts[i] < 1e-12) continue;
                var p = Lerp(a, b, ts[i]);
                var q = Lerp(a, b, ts[i + 1]);
                if (Contains(polygon, Lerp(a, b, (ts[i] + ts[i + 1]) / 2))) result.Add((p, q));
            }
            return result;
        }

        public static XY Lerp(XY a, XY b, double t) => new XY(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        /// <summary>
        /// Area common to two simple polygons: <paramref name="b"/> clipped by each triangle of <paramref name="a"/>.
        /// Polygons that only touch along an edge or at a point give zero.
        /// </summary>
        public static double OverlapArea(IReadOnlyList<XY> a, IReadOnlyList<XY> b)
        {
            var triangles = Triangulate(Normalize(a));
            if (triangles == null) return 0;
            double sum = 0;
            foreach (var corners in triangles)
            {
                // The clipping window must run counter-clockwise.
                var triangle = SignedArea(corners) < 0 ? corners.Reverse().ToArray() : corners;
                var piece = ClipByConvex(b, triangle);
                if (piece.Count >= 3) sum += Area(piece);
            }
            return sum;
        }

        /// <summary>Whether the polygon edges cross themselves (a simple check, O(n²)).</summary>
        public static bool IsSelfIntersecting(IReadOnlyList<XY> polygon)
        {
            int n = polygon.Count;
            for (int i = 0; i < n; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    var c = polygon[j];
                    var d = polygon[(j + 1) % n];
                    double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
                    if (((o1 > Epsilon && o2 < -Epsilon) || (o1 < -Epsilon && o2 > Epsilon)) &&
                        ((o3 > Epsilon && o4 < -Epsilon) || (o3 < -Epsilon && o4 > Epsilon)))
                        return true;
                }
            }
            return false;
        }
    }
}
