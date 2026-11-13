using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Триангуляция Делоне (TIN) поверхности с учётом структурных линий (рёбра-ограничения).
    /// Z в точке — линейная интерполяция внутри треугольника; вне треугольников — «нет данных»
    /// (null), без экстраполяции. Внешняя граница (если задана) — точная маска: снаружи
    /// границы данных нет, внутри — интерполяция по треугольникам (они обрезаются границей).
    /// </summary>
    public sealed class Tin
    {
        private readonly XY[] points;
        private readonly double[] z;
        private readonly string[] ids;
        private readonly int[] triangles;
        private readonly TriangleIndex index;
        private readonly HashSet<long> constrained;

        private Tin(XY[] points, double[] z, string[] ids, int[] triangles, List<int> duplicates, IReadOnlyList<XY> boundary,
            HashSet<long> constrained, List<string> constraintProblems)
        {
            this.points = points;
            this.z = z;
            this.ids = ids;
            this.triangles = triangles;
            this.constrained = constrained;
            Duplicates = duplicates;
            Boundary = boundary;
            ConstraintProblems = constraintProblems;
            index = new TriangleIndex(points, triangles);
        }

        public int PointCount => points.Length;
        public int TriangleCount => triangles.Length / 3;
        /// <summary>Indices of input points skipped because another point has exactly the same XY.</summary>
        public IReadOnlyList<int> Duplicates { get; }
        /// <summary>Normalised outline of the surface (counter-clockwise) or null.</summary>
        public IReadOnlyList<XY> Boundary { get; }
        /// <summary>Structure-line segments that could not be honoured (crossing lines, points left out).</summary>
        public IReadOnlyList<string> ConstraintProblems { get; }
        public int ConstrainedEdgeCount => constrained.Count;

        public XY Point(int i) => points[i];
        public double Z(int i) => z[i];
        public string Id(int i) => ids[i];

        /// <summary>Vertices of triangle t (counter-clockwise), as input point indices.</summary>
        public (int A, int B, int C) Triangle(int t) => (triangles[3 * t], triangles[3 * t + 1], triangles[3 * t + 2]);

        public bool IsConstrained(int a, int b) => constrained.Contains(UndirectedKey(a, b));

        internal static long UndirectedKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

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

        /// <summary>Edges for the drawing: clipped by the outline when it is set; structure-line edges flagged.</summary>
        public IEnumerable<(XY A, XY B, bool Constrained)> DisplayEdges()
        {
            foreach (var (a, b) in Edges())
            {
                bool isConstrained = constrained.Contains(UndirectedKey(a, b));
                if (Boundary == null)
                {
                    yield return (points[a], points[b], isConstrained);
                    continue;
                }
                foreach (var (p, q) in Geometry.ClipSegment(points[a], points[b], Boundary)) yield return (p, q, isConstrained);
            }
        }

        /// <summary>Z by linear interpolation in the triangle containing the point; null outside the surface.</summary>
        public double? ZAt(XY p)
        {
            if (Boundary != null && !Geometry.Contains(Boundary, p)) return null;
            int t = Locate(p);
            return t < 0 ? (double?)null : ZInTriangle(t, p);
        }

        /// <summary>Triangle that contains the point (on an edge — any of the two), or −1.</summary>
        public int Locate(XY p)
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
                return t;
            }
            return -1;
        }

        /// <summary>The plane of triangle t evaluated at p (also slightly outside it: used for clipped pieces).</summary>
        public double ZInTriangle(int t, XY p)
        {
            int ia = triangles[3 * t], ib = triangles[3 * t + 1], ic = triangles[3 * t + 2];
            var a = points[ia];
            var b = points[ib];
            var c = points[ic];
            double area = Geometry.Orient(a, b, c);
            double wa = Geometry.Orient(b, c, p) / area;
            double wb = Geometry.Orient(c, a, p) / area;
            return wa * z[ia] + wb * z[ib] + (1 - wa - wb) * z[ic];
        }

        /// <summary>Triangles whose bounding boxes may overlap the box.</summary>
        public IEnumerable<int> TrianglesInBox(double minX, double minY, double maxX, double maxY) =>
            index.InBox(minX, minY, maxX, maxY, TriangleCount);

        public static Tin Build(IReadOnlyList<XY> xy, IReadOnlyList<double> elevations, IReadOnlyList<string> pointIds = null,
            IReadOnlyList<XY> boundary = null, IReadOnlyList<(int A, int B)> constraints = null, Func<int, string> describe = null)
        {
            if (xy == null) throw new ArgumentNullException(nameof(xy));
            if (elevations == null || elevations.Count != xy.Count) throw new ArgumentException("Число отметок не совпадает с числом точек.");
            var p = xy.ToArray();
            var z = elevations.ToArray();
            var ids = pointIds?.ToArray() ?? new string[p.Length];
            var builder = new DelaunayBuilder(p);
            var triangles = builder.Build();
            var constrained = new HashSet<long>();
            var problems = new List<string>();
            if (constraints != null && constraints.Count > 0 && triangles.Count > 0)
            {
                string Name(int i) => describe?.Invoke(i) ?? ("№ " + (i + 1));
                var inserter = new ConstraintInserter(builder, triangles, Name);
                foreach (var (a, b) in constraints)
                {
                    if (a < 0 || b < 0 || a >= p.Length || b >= p.Length || a == b) continue;
                    int ra = builder.Kept(a), rb = builder.Kept(b);
                    if (ra < 0 || rb < 0)
                    {
                        problems.Add("отрезок структурной линии " + Name(a) + " — " + Name(b) + ": точка " + Name(ra < 0 ? a : b) +
                                     " не вошла в триангуляцию (лежит на прямой с соседними), отрезок пропущен");
                        continue;
                    }
                    inserter.Insert(ra, rb);
                }
                inserter.LegalizeAll();
                triangles = inserter.Triangles;
                constrained = inserter.Constrained;
                problems.AddRange(inserter.Problems);
            }
            List<XY> outline = boundary != null && boundary.Count >= 3 ? Geometry.Normalize(boundary) : null;
            if (outline != null && outline.Count < 3) outline = null;
            return new Tin(p, z, ids, triangles.ToArray(), builder.Duplicates, outline, constrained, problems);
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

            internal IEnumerable<int> InBox(double minX, double minY, double maxX, double maxY, int count)
            {
                if (cells.Count == 0) yield break;
                long x0 = Key(minX - ox), x1 = Key(maxX - ox), y0 = Key(minY - oy), y1 = Key(maxY - oy);
                var seen = new HashSet<int>();
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > cells.Count)
                {
                    foreach (var list in cells.Values)
                    foreach (int t in list)
                        if (seen.Add(t)) yield return t;
                    yield break;
                }
                for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                {
                    if (!cells.TryGetValue((x, y), out var list)) continue;
                    foreach (int t in list)
                        if (seen.Add(t)) yield return t;
                }
            }
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
        private readonly Dictionary<int, int> representative = new Dictionary<int, int>();
        private readonly HashSet<int> skipped = new HashSet<int>();
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
        internal double[] X => x;
        internal double[] Y => y;
        internal int Count => source.Length;

        /// <summary>The vertex of the triangulation that stands for input point i (its exact duplicate), or −1.</summary>
        internal int Kept(int i)
        {
            int r = representative.TryGetValue(i, out int value) ? value : i;
            return skipped.Contains(r) ? -1 : r;
        }

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private double Orient(int a, int b, int c) => (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);

        /// <summary>&gt; 0 when d is strictly inside the circumcircle of counter-clockwise abc.</summary>
        internal static bool InCircle(double[] x, double[] y, int a, int b, int c, int d)
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

        private bool InCircle(int a, int b, int c, int d) => InCircle(x, y, a, b, c, d);

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
                if (unique.Count > 0 && x[unique[unique.Count - 1]] == x[i] && y[unique[unique.Count - 1]] == y[i])
                {
                    Duplicates.Add(i);
                    representative[i] = unique[unique.Count - 1];
                }
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
                else
                {
                    Duplicates.Add(unique[i]);
                    skipped.Add(unique[i]);
                }
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

    /// <summary>
    /// Structure lines (рёбра-ограничения) inserted into a Delaunay triangulation by edge flips
    /// (S. W. Sloan, 1993): the edges crossed by the segment are flipped until the segment is an
    /// edge; then all other edges are made Delaunay again (Lawson flips, constrained edges kept).
    /// A segment through a vertex is split at it; a segment crossing another structure line is
    /// skipped and reported (the elevation at the crossing would be ambiguous).
    /// </summary>
    internal sealed class ConstraintInserter
    {
        /// <summary>A vertex closer than this to a segment is taken as lying on it, m.</summary>
        private const double OnLine = 1e-6;
        private readonly double[] x, y;
        private readonly List<int> v;
        private readonly Dictionary<long, int> edge = new Dictionary<long, int>();
        private readonly int[] incident;
        private readonly Func<int, string> name;

        internal ConstraintInserter(DelaunayBuilder builder, List<int> triangles, Func<int, string> name)
        {
            x = builder.X;
            y = builder.Y;
            v = new List<int>(triangles);
            this.name = name;
            incident = Enumerable.Repeat(-1, builder.Count).ToArray();
            for (int t = 0; t < v.Count / 3; t++) Register(t);
        }

        internal HashSet<long> Constrained { get; } = new HashSet<long>();
        internal List<string> Problems { get; } = new List<string>();
        internal List<int> Triangles => v;

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private void Register(int t)
        {
            for (int k = 0; k < 3; k++)
            {
                edge[Key(v[3 * t + k], v[3 * t + (k + 1) % 3])] = t;
                incident[v[3 * t + k]] = t;
            }
        }

        private void Unregister(int t)
        {
            for (int k = 0; k < 3; k++) edge.Remove(Key(v[3 * t + k], v[3 * t + (k + 1) % 3]));
        }

        private double Orient(int a, int b, int c) => (x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]);

        private double Distance(int a, int b) => Math.Sqrt((x[b] - x[a]) * (x[b] - x[a]) + (y[b] - y[a]) * (y[b] - y[a]));

        private int Third(int t, int a, int b)
        {
            for (int k = 0; k < 3; k++)
            {
                int w = v[3 * t + k];
                if (w != a && w != b) return w;
            }
            return -1;
        }

        private bool HasEdge(int a, int b) => edge.ContainsKey(Key(a, b)) || edge.ContainsKey(Key(b, a));

        /// <summary>c — apex of the triangle with a→b (left of the edge), d — apex of the one with b→a.</summary>
        private bool Quad(int a, int b, out int c, out int d)
        {
            c = d = -1;
            if (!edge.TryGetValue(Key(a, b), out int t) || !edge.TryGetValue(Key(b, a), out int u)) return false;
            c = Third(t, a, b);
            d = Third(u, a, b);
            return c >= 0 && d >= 0;
        }

        /// <summary>Replaces diagonal a–b of the convex quadrilateral by c–d.</summary>
        private bool Flip(int a, int b, out int c, out int d)
        {
            c = d = -1;
            if (!edge.TryGetValue(Key(a, b), out int t) || !edge.TryGetValue(Key(b, a), out int u)) return false;
            c = Third(t, a, b);
            d = Third(u, a, b);
            // t = (a, b, c), u = (b, a, d), both counter-clockwise; new (c, a, d) and (c, d, b).
            if (Orient(c, a, d) <= 0 || Orient(c, d, b) <= 0) return false;
            Unregister(t);
            Unregister(u);
            v[3 * t] = c; v[3 * t + 1] = a; v[3 * t + 2] = d;
            v[3 * u] = c; v[3 * u + 1] = d; v[3 * u + 2] = b;
            Register(t);
            Register(u);
            return true;
        }

        private bool Crosses(int a, int b, int p, int q)
        {
            if (a == p || a == q || b == p || b == q) return false;
            double o1 = Orient(p, q, a), o2 = Orient(p, q, b);
            if (!((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0))) return false;
            double o3 = Orient(a, b, p), o4 = Orient(a, b, q);
            return (o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0);
        }

        /// <summary>w lies on the open segment p–q (within <see cref="OnLine"/>).</summary>
        private bool OnSegment(int w, int p, int q)
        {
            if (w == p || w == q) return false;
            double length = Distance(p, q);
            if (length <= 0) return false;
            if (Math.Abs(Orient(p, q, w)) / length > OnLine) return false;
            double t = ((x[w] - x[p]) * (x[q] - x[p]) + (y[w] - y[p]) * (y[q] - y[p])) / (length * length);
            return t * length > OnLine && (1 - t) * length > OnLine;
        }

        private List<int> AroundVertex(int p)
        {
            var result = new List<int>();
            int start = incident[p];
            if (start < 0) return result;
            int t = start;
            int guard = 0;
            // Counter-clockwise: across edge (c, p) of triangle (p, b, c).
            while (t >= 0 && guard++ < 10000)
            {
                result.Add(t);
                int c = Next(t, p, 2);
                if (!edge.TryGetValue(Key(p, c), out int nextTriangle)) { t = -1; break; }
                if (nextTriangle == start) { t = -2; break; }
                t = nextTriangle;
            }
            if (t == -2) return result;
            // Hull vertex: also clockwise from the start, across edge (p, b).
            t = start;
            guard = 0;
            while (guard++ < 10000)
            {
                int b = Next(t, p, 1);
                if (!edge.TryGetValue(Key(b, p), out int previous)) break;
                result.Add(previous);
                t = previous;
            }
            return result;
        }

        /// <summary>The vertex <paramref name="step"/> positions after p in triangle t (1 — next, 2 — previous).</summary>
        private int Next(int t, int p, int step)
        {
            for (int k = 0; k < 3; k++)
                if (v[3 * t + k] == p) return v[3 * t + (k + step) % 3];
            return -1;
        }

        internal void Insert(int p, int q)
        {
            var pending = new Stack<(int, int)>();
            pending.Push((p, q));
            int guard = 0;
            while (pending.Count > 0 && guard++ < 100000)
            {
                var (a, b) = pending.Pop();
                if (a == b) continue;
                if (HasEdge(a, b))
                {
                    Constrained.Add(Tin.UndirectedKey(a, b));
                    continue;
                }
                var crossed = Walk(a, b, out int through);
                if (through >= 0)
                {
                    pending.Push((through, b));
                    pending.Push((a, through));
                    continue;
                }
                if (crossed == null)
                {
                    Problems.Add("отрезок структурной линии " + name(a) + " — " + name(b) + " не удалось встроить в триангуляцию (вырожденное положение точек)");
                    continue;
                }
                var blocking = crossed.FirstOrDefault(e => Constrained.Contains(Tin.UndirectedKey(e.L, e.R)));
                if (blocking.L != blocking.R)
                {
                    Problems.Add("структурные линии пересекаются: отрезок " + name(a) + " — " + name(b) + " пересекает отрезок " +
                                 name(blocking.L) + " — " + name(blocking.R) + " и пропущен (отметка на пересечении не определена)");
                    continue;
                }
                if (!FlipAway(a, b, crossed))
                    Problems.Add("отрезок структурной линии " + name(a) + " — " + name(b) + " не удалось встроить в триангуляцию");
            }
        }

        /// <summary>
        /// Edges crossed by segment p→q, in order. <paramref name="through"/> — a vertex lying on the
        /// segment (the segment is then split at it); null when the walk failed.
        /// </summary>
        private List<(int L, int R)> Walk(int p, int q, out int through)
        {
            through = -1;
            var crossed = new List<(int L, int R)>();
            int current = -1, left = -1, right = -1;
            foreach (int t in AroundVertex(p))
            {
                int b = Next(t, p, 1), c = Next(t, p, 2);
                if (OnSegment(b, p, q)) { through = b; return null; }
                if (OnSegment(c, p, q)) { through = c; return null; }
                if (Orient(p, b, q) > 0 && Orient(p, c, q) < 0)
                {
                    current = t;
                    right = b;
                    left = c;
                    break;
                }
            }
            if (current < 0) return null;
            int guard = 0;
            while (guard++ < v.Count)
            {
                crossed.Add((left, right));
                int nextTriangle = Across(current, left, right);
                if (nextTriangle < 0) return null;
                int d = Third(nextTriangle, left, right);
                if (d == q) return crossed;
                if (OnSegment(d, p, q)) { through = d; return null; }
                if (Orient(p, q, d) > 0) left = d; else right = d;
                current = nextTriangle;
            }
            return null;
        }

        private int Across(int current, int a, int b)
        {
            if (edge.TryGetValue(Key(a, b), out int t1) && t1 != current) return t1;
            if (edge.TryGetValue(Key(b, a), out int t2) && t2 != current) return t2;
            return -1;
        }

        /// <summary>Sloan: flip the crossed edges (convex quadrilaterals first) until none crosses p–q.</summary>
        private bool FlipAway(int p, int q, List<(int L, int R)> crossed)
        {
            var queue = new Queue<(int, int)>(crossed);
            int limit = 50 * (crossed.Count + 10) * (crossed.Count + 10);
            int guard = 0, idle = 0;
            while (queue.Count > 0 && guard++ < limit)
            {
                var (a, b) = queue.Dequeue();
                if (!HasEdge(a, b)) { idle = 0; continue; }
                if (!Crosses(a, b, p, q)) continue;
                if (!Flip(a, b, out int c, out int d))
                {
                    queue.Enqueue((a, b));
                    if (++idle > queue.Count + 1) return false;
                    continue;
                }
                idle = 0;
                if (Crosses(c, d, p, q)) queue.Enqueue((c, d));
            }
            if (!HasEdge(p, q)) return false;
            Constrained.Add(Tin.UndirectedKey(p, q));
            return true;
        }

        /// <summary>Lawson flips over all edges except the structure lines (constrained Delaunay).</summary>
        internal void LegalizeAll()
        {
            var stack = new Stack<(int, int)>();
            var queued = new HashSet<long>();
            foreach (long key in edge.Keys.ToList())
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
                if (a < b && edge.ContainsKey(Key(b, a)) && queued.Add(Tin.UndirectedKey(a, b))) stack.Push((a, b));
            }
            long guard = 0, limit = 100L * (edge.Count + 10);
            while (stack.Count > 0 && guard++ < limit)
            {
                var (a, b) = stack.Pop();
                queued.Remove(Tin.UndirectedKey(a, b));
                if (Constrained.Contains(Tin.UndirectedKey(a, b))) continue;
                if (!Quad(a, b, out int c, out int d)) continue;
                if (!DelaunayBuilder.InCircle(x, y, a, b, c, d)) continue;
                if (!Flip(a, b, out c, out d)) continue;
                foreach (var (e1, e2) in new[] { (a, d), (d, b), (b, c), (c, a) })
                    if (queued.Add(Tin.UndirectedKey(e1, e2))) stack.Push((e1, e2));
            }
        }
    }
}
