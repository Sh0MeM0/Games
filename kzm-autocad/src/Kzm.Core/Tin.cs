using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Триангуляция Делоне (TIN) поверхности. Z в точке — линейная интерполяция внутри
    /// треугольника; вне треугольников — «нет данных» (null), без экстраполяции.
    /// Внешняя граница (если задана) отсекает треугольники, центр тяжести которых вне её.
    /// </summary>
    public sealed class Tin
    {
        private readonly XY[] points;
        private readonly double[] z;
        private readonly string[] ids;
        private readonly int[] triangles;
        private readonly TriangleIndex index;

        private Tin(XY[] points, double[] z, string[] ids, int[] triangles, List<int> duplicates, IReadOnlyList<XY> boundary, int removedByBoundary)
        {
            this.points = points;
            this.z = z;
            this.ids = ids;
            this.triangles = triangles;
            Duplicates = duplicates;
            Boundary = boundary;
            RemovedByBoundary = removedByBoundary;
            index = new TriangleIndex(points, triangles);
        }

        public int PointCount => points.Length;
        public int TriangleCount => triangles.Length / 3;
        /// <summary>Indices of input points skipped because another point has exactly the same XY.</summary>
        public IReadOnlyList<int> Duplicates { get; }
        public IReadOnlyList<XY> Boundary { get; }
        public int RemovedByBoundary { get; }

        public XY Point(int i) => points[i];
        public double Z(int i) => z[i];
        public string Id(int i) => ids[i];

        /// <summary>Vertices of triangle t (counter-clockwise), as input point indices.</summary>
        public (int A, int B, int C) Triangle(int t) => (triangles[3 * t], triangles[3 * t + 1], triangles[3 * t + 2]);

        public IEnumerable<(int A, int B)> Edges()
        {
            var seen = new HashSet<long>();
            for (int t = 0; t < TriangleCount; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[3 * t + k], b = triangles[3 * t + (k + 1) % 3];
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                if (seen.Add(((long)lo << 32) | (uint)hi)) yield return (lo, hi);
            }
        }

        /// <summary>Z by linear interpolation in the triangle containing the point; null outside the surface.</summary>
        public double? ZAt(XY p)
        {
            foreach (int t in index.Candidates(p))
            {
                var a = points[triangles[3 * t]];
                var b = points[triangles[3 * t + 1]];
                var c = points[triangles[3 * t + 2]];
                double area = Geometry.Orient(a, b, c);
                if (area <= 0) continue;
                double wa = Geometry.Orient(b, c, p) / area;
                double wb = Geometry.Orient(c, a, p) / area;
                double wc = 1 - wa - wb;
                const double tolerance = -1e-9;
                if (wa < tolerance || wb < tolerance || wc < tolerance) continue;
                return wa * z[triangles[3 * t]] + wb * z[triangles[3 * t + 1]] + wc * z[triangles[3 * t + 2]];
            }
            return null;
        }

        public static Tin Build(IReadOnlyList<XY> xy, IReadOnlyList<double> elevations, IReadOnlyList<string> pointIds = null,
            IReadOnlyList<XY> boundary = null)
        {
            if (xy == null) throw new ArgumentNullException(nameof(xy));
            if (elevations == null || elevations.Count != xy.Count) throw new ArgumentException("Число отметок не совпадает с числом точек.");
            var p = xy.ToArray();
            var z = elevations.ToArray();
            var ids = pointIds?.ToArray() ?? new string[p.Length];
            var builder = new DelaunayBuilder(p);
            var triangles = builder.Build();
            int removed = 0;
            List<XY> outline = null;
            if (boundary != null && boundary.Count >= 3)
            {
                outline = Geometry.Normalize(boundary);
                var kept = new List<int>(triangles.Count);
                for (int t = 0; t < triangles.Count; t += 3)
                {
                    var a = p[triangles[t]];
                    var b = p[triangles[t + 1]];
                    var c = p[triangles[t + 2]];
                    var centre = new XY((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
                    if (Geometry.Contains(outline, centre)) kept.AddRange(new[] { triangles[t], triangles[t + 1], triangles[t + 2] });
                    else removed++;
                }
                triangles = kept;
            }
            return new Tin(p, z, ids, triangles.ToArray(), builder.Duplicates, outline, removed);
        }

        /// <summary>Uniform grid over triangle bounding boxes for point location.</summary>
        private sealed class TriangleIndex
        {
            private readonly Dictionary<(long, long), List<int>> cells = new Dictionary<(long, long), List<int>>();
            private readonly double cell;
            private readonly double ox, oy;

            internal TriangleIndex(XY[] points, int[] triangles)
            {
                int count = triangles.Length / 3;
                if (count == 0) { cell = 1; return; }
                ox = points.Min(q => q.X);
                oy = points.Min(q => q.Y);
                double sum = 0;
                for (int t = 0; t < count; t++)
                {
                    var a = points[triangles[3 * t]];
                    var b = points[triangles[3 * t + 1]];
                    var c = points[triangles[3 * t + 2]];
                    sum += Math.Max(Math.Max(a.DistanceTo(b), b.DistanceTo(c)), c.DistanceTo(a));
                }
                cell = Math.Max(sum / count, 1e-6);
                for (int t = 0; t < count; t++)
                {
                    var a = points[triangles[3 * t]];
                    var b = points[triangles[3 * t + 1]];
                    var c = points[triangles[3 * t + 2]];
                    long x0 = Key(Math.Min(a.X, Math.Min(b.X, c.X)) - ox), x1 = Key(Math.Max(a.X, Math.Max(b.X, c.X)) - ox);
                    long y0 = Key(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - oy), y1 = Key(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - oy);
                    for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                    {
                        if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = new List<int>();
                        list.Add(t);
                    }
                }
            }

            private long Key(double value) => (long)Math.Floor(value / cell);

            internal IEnumerable<int> Candidates(XY p) =>
                cells.TryGetValue((Key(p.X - ox), Key(p.Y - oy)), out var list) ? list : Enumerable.Empty<int>();
        }
    }

    /// <summary>
    /// Incremental Delaunay triangulation: points sorted by X (then Y) are added outside
    /// the current convex hull, connected to the visible hull edges, and the new edges are
    /// legalised by Lawson flips (empty circumcircle). No super triangle: the convex hull is exact.
    /// </summary>
    internal sealed class DelaunayBuilder
    {
        private readonly XY[] source;
        private readonly double[] x, y;
        private readonly List<int> v = new List<int>();
        private readonly List<int> adj = new List<int>();
        private readonly Dictionary<long, int> hullEdge = new Dictionary<long, int>();
        private int[] next, prev;

        internal DelaunayBuilder(XY[] points)
        {
            source = points;
            // Local coordinates keep the precision of survey coordinates (~5 000 000 m).
            double cx = points.Length == 0 ? 0 : points.Average(p => p.X);
            double cy = points.Length == 0 ? 0 : points.Average(p => p.Y);
            x = points.Select(p => p.X - cx).ToArray();
            y = points.Select(p => p.Y - cy).ToArray();
        }

        internal List<int> Duplicates { get; } = new List<int>();

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private double Orient(int a, int b, int c) => (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);

        /// <summary>&gt; 0 when d is strictly inside the circumcircle of counter-clockwise abc.</summary>
        private bool InCircle(int a, int b, int c, int d)
        {
            double adx = x[a] - x[d], ady = y[a] - y[d];
            double bdx = x[b] - x[d], bdy = y[b] - y[d];
            double cdx = x[c] - x[d], cdy = y[c] - y[d];
            double alift = adx * adx + ady * ady, blift = bdx * bdx + bdy * bdy, clift = cdx * cdx + cdy * cdy;
            double det = alift * (bdx * cdy - cdx * bdy) + blift * (cdx * ady - adx * cdy) + clift * (adx * bdy - bdx * ady);
            double permanent = alift * (Math.Abs(bdx * cdy) + Math.Abs(cdx * bdy)) +
                               blift * (Math.Abs(cdx * ady) + Math.Abs(adx * cdy)) +
                               clift * (Math.Abs(adx * bdy) + Math.Abs(bdx * ady));
            return det > 1e-12 * permanent;
        }

        private int AddTriangle(int a, int b, int c, int na, int nb, int nc)
        {
            int t = v.Count / 3;
            v.Add(a); v.Add(b); v.Add(c);
            adj.Add(na); adj.Add(nb); adj.Add(nc);
            return t;
        }

        private void ReplaceNeighbour(int t, int from, int to)
        {
            if (t < 0) return;
            for (int k = 0; k < 3; k++)
                if (adj[3 * t + k] == from) { adj[3 * t + k] = to; return; }
        }

        internal List<int> Build()
        {
            int n = source.Length;
            var order = Enumerable.Range(0, n).OrderBy(i => x[i]).ThenBy(i => y[i]).ToList();
            // Exact XY duplicates are skipped (reported), they would make zero-area triangles.
            var unique = new List<int>(n);
            foreach (int i in order)
            {
                if (unique.Count > 0 && x[unique[unique.Count - 1]] == x[i] && y[unique[unique.Count - 1]] == y[i]) Duplicates.Add(i);
                else unique.Add(i);
            }
            var result = new List<int>();
            if (unique.Count < 3) return result;

            // Leading collinear chain, then the first point off the line.
            double scale = Math.Max(1e-9, Math.Max(x.Max() - x.Min(), y.Max() - y.Min()));
            double flat = 1e-12 * scale * scale;
            int k = 2;
            while (k < unique.Count && Math.Abs(Orient(unique[0], unique[1], unique[k])) <= flat) k++;
            if (k == unique.Count) return result; // all points on one line
            next = new int[n];
            prev = new int[n];
            int apex = unique[k];
            bool left = Orient(unique[0], unique[k - 1], apex) > 0;
            int previousTriangle = -1;
            for (int i = 0; i + 1 < k; i++)
            {
                int a = unique[i], b = unique[i + 1];
                // Apex at index 0; the hull edge of the chain is opposite it.
                int t = left ? AddTriangle(apex, a, b, -1, -1, previousTriangle) : AddTriangle(apex, b, a, -1, previousTriangle, -1);
                if (previousTriangle >= 0) ReplaceNeighbour(previousTriangle, -2, t);
                // The edge shared with the next triangle is (apex, b); mark it to be filled.
                if (left) adj[3 * t + 1] = -2; else adj[3 * t + 2] = -2;
                if (left) hullEdge[Key(a, b)] = t; else hullEdge[Key(b, a)] = t;
                previousTriangle = t;
            }
            ReplaceNeighbour(previousTriangle, -2, -1);
            int first = unique[0], last = unique[k - 1];
            int firstTriangle = 0;
            if (left)
            {
                for (int i = 0; i + 1 < k; i++) { next[unique[i]] = unique[i + 1]; prev[unique[i + 1]] = unique[i]; }
                next[last] = apex; prev[apex] = last; next[apex] = first; prev[first] = apex;
                hullEdge[Key(last, apex)] = previousTriangle;
                hullEdge[Key(apex, first)] = firstTriangle;
            }
            else
            {
                for (int i = 0; i + 1 < k; i++) { next[unique[i + 1]] = unique[i]; prev[unique[i]] = unique[i + 1]; }
                next[first] = apex; prev[apex] = first; next[apex] = last; prev[last] = apex;
                hullEdge[Key(first, apex)] = firstTriangle;
                hullEdge[Key(apex, last)] = previousTriangle;
            }

            int lastInserted = apex;
            for (int i = k + 1; i < unique.Count; i++)
            {
                if (Insert(unique[i], lastInserted, flat)) lastInserted = unique[i];
                else Duplicates.Add(unique[i]);
            }
            for (int t = 0; t < v.Count / 3; t++) result.AddRange(new[] { v[3 * t], v[3 * t + 1], v[3 * t + 2] });
            return result;
        }

        /// <summary>Adds point p (to the right of all inserted points) outside the hull.</summary>
        private bool Insert(int p, int lastInserted, double flat)
        {
            int start = lastInserted, end = lastInserted;
            int guard = next.Length + 5;
            while (Orient(prev[start], start, p) < -flat && guard-- > 0) start = prev[start];
            guard = next.Length + 5;
            while (Orient(end, next[end], p) < -flat && guard-- > 0) end = next[end];
            if (start == end) return false; // p lies on the hull line: degenerate, skipped and reported

            // For every visible hull edge a→b a triangle (p, b, a):
            //   opposite p — edge (b, a), shared with the old hull triangle;
            //   opposite b — edge (a, p), shared with the previous new triangle;
            //   opposite a — edge (p, b), shared with the next new triangle.
            var created = new List<int>();
            for (int a = start; a != end; a = next[a])
            {
                int b = next[a];
                int outer = hullEdge[Key(a, b)];
                hullEdge.Remove(Key(a, b));
                int t = AddTriangle(p, b, a, outer, -1, -1);
                FixOuter(outer, a, b, t);
                created.Add(t);
            }
            for (int i = 0; i < created.Count; i++)
            {
                adj[3 * created[i] + 1] = i > 0 ? created[i - 1] : -1;
                adj[3 * created[i] + 2] = i + 1 < created.Count ? created[i + 1] : -1;
            }
            // Hull: start → p → end; vertices between are inside now.
            next[start] = p; prev[p] = start; next[p] = end; prev[end] = p;
            hullEdge[Key(start, p)] = created[0];
            hullEdge[Key(p, end)] = created[created.Count - 1];
            var stack = new Stack<int>(created);
            while (stack.Count > 0) Legalize(stack.Pop(), stack);
            return true;
        }

        /// <summary>The old hull triangle that owned edge a→b now borders t across that edge.</summary>
        private void FixOuter(int outer, int a, int b, int t)
        {
            for (int k = 0; k < 3; k++)
            {
                int va = v[3 * outer + (k + 1) % 3], vb = v[3 * outer + (k + 2) % 3];
                if (va == a && vb == b) { adj[3 * outer + k] = t; return; }
            }
        }

        /// <summary>Triangle t has the new point at index 0; checks the edge opposite it.</summary>
        private void Legalize(int t, Stack<int> stack)
        {
            int p0 = v[3 * t], p1 = v[3 * t + 1], p2 = v[3 * t + 2];
            int u = adj[3 * t];
            if (u < 0) return;
            int j = -1;
            for (int k = 0; k < 3; k++) if (adj[3 * u + k] == t) j = k;
            if (j < 0) return;
            int q = v[3 * u + j];
            if (!InCircle(p0, p1, p2, q)) return;
            int a1 = adj[3 * t + 1], a2 = adj[3 * t + 2];
            int b1 = adj[3 * u + (j + 1) % 3], b2 = adj[3 * u + (j + 2) % 3];
            // t' = (p0, p1, q), u' = (p0, q, p2)
            v[3 * t] = p0; v[3 * t + 1] = p1; v[3 * t + 2] = q;
            adj[3 * t] = b1; adj[3 * t + 1] = u; adj[3 * t + 2] = a2;
            v[3 * u] = p0; v[3 * u + 1] = q; v[3 * u + 2] = p2;
            adj[3 * u] = b2; adj[3 * u + 1] = a1; adj[3 * u + 2] = t;
            if (b1 >= 0) ReplaceNeighbour(b1, u, t); else hullEdge[Key(p1, q)] = t;
            if (a1 >= 0) ReplaceNeighbour(a1, t, u); else hullEdge[Key(p2, p0)] = u;
            stack.Push(t);
            stack.Push(u);
        }
    }
}
