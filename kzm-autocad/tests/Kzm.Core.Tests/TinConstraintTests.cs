using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Structure lines (рёбра-ограничения) in the triangulation.</summary>
    public class TinConstraintTests
    {
        private static bool HasEdge(Tin tin, int a, int b) =>
            tin.Edges().Any(e => (e.A == Math.Min(a, b) && e.B == Math.Max(a, b)));

        /// <summary>Valid triangulation of the hull; every edge that is not a structure line is locally Delaunay.</summary>
        private static void AssertConstrainedDelaunay(Tin tin, IReadOnlyList<XY> points)
        {
            var origin = points[0];
            var local = points.Select(p => new XY(p.X - origin.X, p.Y - origin.Y)).ToList();
            var owner = new Dictionary<(int, int), int>();
            double area = 0;
            for (int t = 0; t < tin.TriangleCount; t++)
            {
                var (a, b, c) = tin.Triangle(t);
                double twice = Geometry.Orient(local[a], local[b], local[c]);
                Assert.True(twice > 0, "triangle " + t + " is not counter-clockwise");
                area += twice / 2;
                foreach (var (u, w) in new[] { (a, b), (b, c), (c, a) })
                {
                    Assert.False(owner.ContainsKey((u, w)), "directed edge used twice");
                    owner[(u, w)] = t;
                }
            }
            foreach (var pair in owner)
            {
                var (a, b) = pair.Key;
                if (!owner.TryGetValue((b, a), out int u) || a > b) continue;
                if (tin.IsConstrained(a, b)) continue;
                var (t0, t1, t2) = tin.Triangle(pair.Value);
                int c = new[] { t0, t1, t2 }.First(i => i != a && i != b);
                var (u0, u1, u2) = tin.Triangle(u);
                int d = new[] { u0, u1, u2 }.First(i => i != a && i != b);
                var pa = local[a]; var pb = local[b]; var pc = local[c]; var pd = local[d];
                double adx = pa.X - pd.X, ady = pa.Y - pd.Y, bdx = pb.X - pd.X, bdy = pb.Y - pd.Y, cdx = pc.X - pd.X, cdy = pc.Y - pd.Y;
                double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) + (bdx * bdx + bdy * bdy) * (cdx * ady - adx * cdy) +
                             (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
                double scale = Math.Pow(Math.Max(1, pa.DistanceTo(pd) + pb.DistanceTo(pd) + pc.DistanceTo(pd)), 4);
                Assert.True(det <= 1e-9 * scale, "edge " + a + "-" + b + " is not Delaunay");
            }
            var unique = local.Distinct().ToList();
            double hull = Hull(unique);
            Assert.InRange(Math.Abs(hull - area), 0, 1e-9 * hull + 1e-9);
        }

        private static double Hull(List<XY> p)
        {
            p = p.OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            var lower = new List<XY>();
            foreach (var q in p)
            {
                while (lower.Count >= 2 && Geometry.Orient(lower[lower.Count - 2], lower[lower.Count - 1], q) <= 0) lower.RemoveAt(lower.Count - 1);
                lower.Add(q);
            }
            var upper = new List<XY>();
            for (int i = p.Count - 1; i >= 0; i--)
            {
                while (upper.Count >= 2 && Geometry.Orient(upper[upper.Count - 2], upper[upper.Count - 1], p[i]) <= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(p[i]);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            return Geometry.Area(lower.Concat(upper).ToList());
        }

        [Fact]
        public void StructureLineForcesTheLongDiagonal()
        {
            var points = new List<XY> { new XY(0, 0), new XY(10, -1), new XY(20, 0), new XY(10, 1) };
            var z = new List<double> { 0, 5, 20, 5 };
            var free = Tin.Build(points, z);
            Assert.True(HasEdge(free, 1, 3));
            Assert.Equal(5, free.ZAt(new XY(10, 0)).Value, 9);

            var tin = Tin.Build(points, z, null, null, new[] { (0, 2) });
            Assert.True(HasEdge(tin, 0, 2));
            Assert.True(tin.IsConstrained(0, 2));
            Assert.Empty(tin.ConstraintProblems);
            // Along the line the elevation is linear between its ends: 0 → 20.
            Assert.Equal(10, tin.ZAt(new XY(10, 0)).Value, 9);
            Assert.Equal(5, tin.ZAt(new XY(5, 0)).Value, 9);
        }

        [Theory]
        [InlineData(5, 200, 15)]
        [InlineData(6, 800, 40)]
        [InlineData(7, 3000, 120)]
        public void MonotoneChainThroughRandomPoints(int seed, int count, int chain)
        {
            var random = new Random(seed);
            var points = Enumerable.Range(0, count)
                .Select(_ => new XY(5_500_000 + random.NextDouble() * 500, 4_300_000 + random.NextDouble() * 300)).ToList();
            // An x-monotone polyline through random vertices never crosses itself.
            var order = Enumerable.Range(0, count).OrderBy(_ => random.Next()).Take(chain).OrderBy(i => points[i].X).ToList();
            var constraints = new List<(int, int)>();
            for (int i = 0; i + 1 < order.Count; i++) constraints.Add((order[i], order[i + 1]));
            var tin = Tin.Build(points, points.Select(p => p.Y * 0.001).ToList(), null, null, constraints);
            Assert.Empty(tin.ConstraintProblems);
            AssertConstrainedDelaunay(tin, points);
            // Each segment is an edge, or a chain of edges through vertices that lie on it.
            foreach (var (a, b) in constraints)
                Assert.True(tin.IsConstrained(a, b) || points.Any(p => !p.Equals(points[a]) && !p.Equals(points[b]) &&
                    Geometry.DistanceToSegment(p, points[a], points[b]) < 1e-6), "segment " + a + "-" + b + " missing");
            Assert.True(tin.ConstrainedEdgeCount >= constraints.Count);
        }

        [Fact]
        public void SegmentThroughAVertexIsSplit()
        {
            var points = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(20, 0), new XY(10, 6), new XY(10, -6), new XY(0, 9), new XY(20, -9) };
            var tin = Tin.Build(points, points.Select(_ => 1.0).ToList(), null, null, new[] { (0, 2) });
            Assert.Empty(tin.ConstraintProblems);
            Assert.True(tin.IsConstrained(0, 1));
            Assert.True(tin.IsConstrained(1, 2));
            AssertConstrainedDelaunay(tin, points);
        }

        [Fact]
        public void CrossingStructureLinesAreReported()
        {
            var points = new List<XY> { new XY(0, 0), new XY(20, 20), new XY(0, 20), new XY(20, 0), new XY(10, 30), new XY(10, -10) };
            var tin = Tin.Build(points, points.Select(_ => 1.0).ToList(), null, null, new[] { (0, 1), (2, 3) },
                i => "т." + (i + 1));
            Assert.True(tin.IsConstrained(0, 1));
            Assert.False(tin.IsConstrained(2, 3));
            Assert.Single(tin.ConstraintProblems);
            Assert.Contains("пересекаются", tin.ConstraintProblems[0]);
            Assert.Contains("т.3", tin.ConstraintProblems[0]);
            AssertConstrainedDelaunay(tin, points);
        }

        [Fact]
        public void ConstraintOnDuplicatePointUsesTheKeptOne()
        {
            var points = new List<XY> { new XY(0, 0), new XY(10, -1), new XY(20, 0), new XY(10, 1), new XY(20, 0) };
            var tin = Tin.Build(points, new[] { 0.0, 5, 20, 5, 20 }, null, null, new[] { (0, 4) });
            Assert.Single(tin.Duplicates);
            Assert.True(tin.IsConstrained(0, 2));
            Assert.Equal(10, tin.ZAt(new XY(10, 0)).Value, 9);
        }

        [Fact]
        public void ClosedOutlineOfAPlatformKeepsItPlanar()
        {
            // A platform (plane) inside rough ground: its contour as a closed structure line keeps
            // the platform exactly planar although outside points are near.
            var random = new Random(3);
            var points = new List<XY>();
            var z = new List<double>();
            for (int i = 0; i < 300; i++)
            {
                var p = new XY(random.NextDouble() * 100, random.NextDouble() * 100);
                if (p.X > 28 && p.X < 72 && p.Y > 28 && p.Y < 72) continue;
                points.Add(p);
                z.Add(100 + random.NextDouble());
            }
            Func<XY, double> plane = p => 150 + 0.01 * p.X + 0.02 * p.Y;
            var contour = new[] { new XY(30, 30), new XY(70, 31), new XY(69, 70), new XY(31, 69) };
            int first = points.Count;
            foreach (var p in contour) { points.Add(p); z.Add(plane(p)); }
            var constraints = new List<(int, int)>();
            for (int i = 0; i < contour.Length; i++) constraints.Add((first + i, first + (i + 1) % contour.Length));
            var tin = Tin.Build(points, z, null, null, constraints);
            Assert.Empty(tin.ConstraintProblems);
            for (int i = 0; i < 200; i++)
            {
                var q = new XY(32 + random.NextDouble() * 36, 32 + random.NextDouble() * 36);
                Assert.Equal(plane(q), tin.ZAt(q).Value, 9);
            }
        }
    }
}
