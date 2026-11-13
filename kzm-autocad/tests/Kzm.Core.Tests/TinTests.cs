using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class TinTests
    {
        private static double HullArea(IReadOnlyList<XY> points)
        {
            var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (p.Count < 3) return 0;
            var lower = new List<XY>();
            foreach (var q in p)
            {
                while (lower.Count >= 2 && Geometry.Orient(lower[lower.Count - 2], lower[lower.Count - 1], q) <= 0) lower.RemoveAt(lower.Count - 1);
                lower.Add(q);
            }
            var upper = new List<XY>();
            for (int i = p.Count - 1; i >= 0; i--)
            {
                var q = p[i];
                while (upper.Count >= 2 && Geometry.Orient(upper[upper.Count - 2], upper[upper.Count - 1], q) <= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(q);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            return Geometry.Area(lower.Concat(upper).ToList());
        }

        private static void AssertValid(Tin tin, IReadOnlyList<XY> absolute, bool checkDelaunay)
        {
            // Checks in local coordinates: shoelace sums in absolute survey coordinates lose ~1e-2 m².
            var origin = absolute[0];
            var points = absolute.Select(p => new XY(p.X - origin.X, p.Y - origin.Y)).ToList();
            double area = 0;
            for (int t = 0; t < tin.TriangleCount; t++)
            {
                var (a, b, c) = tin.Triangle(t);
                double twice = Geometry.Orient(points[a], points[b], points[c]);
                Assert.True(twice > 0, "triangle " + t + " is not counter-clockwise");
                area += twice / 2;
                if (!checkDelaunay) continue;
                // Empty circumcircle: no other point strictly inside (relative tolerance).
                var pa = points[a]; var pb = points[b]; var pc = points[c];
                for (int k = 0; k < points.Count; k++)
                {
                    if (k == a || k == b || k == c) continue;
                    var d = points[k];
                    double adx = pa.X - d.X, ady = pa.Y - d.Y, bdx = pb.X - d.X, bdy = pb.Y - d.Y, cdx = pc.X - d.X, cdy = pc.Y - d.Y;
                    double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) + (bdx * bdx + bdy * bdy) * (cdx * ady - adx * cdy) +
                                 (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
                    double scale = Math.Pow(Math.Max(1, pa.DistanceTo(d) + pb.DistanceTo(d) + pc.DistanceTo(d)), 4);
                    Assert.True(det <= 1e-9 * scale, "point " + k + " inside the circumcircle of triangle " + t);
                }
            }
            // Areas are summed in absolute survey coordinates (~5·10⁶ m): compare relatively.
            double hull = HullArea(points);
            Assert.InRange(Math.Abs(hull - area), 0, 1e-9 * hull + 1e-9);
        }

        [Theory]
        [InlineData(1, 10)]
        [InlineData(2, 60)]
        [InlineData(3, 300)]
        [InlineData(4, 300)]
        public void RandomPointsGiveDelaunayCoveringTheConvexHull(int seed, int count)
        {
            var random = new Random(seed);
            var points = Enumerable.Range(0, count)
                .Select(_ => new XY(5_500_000 + random.NextDouble() * 400, 4_300_000 + random.NextDouble() * 250)).ToList();
            var tin = Tin.Build(points, points.Select(_ => 100.0).ToList());
            AssertValid(tin, points, true);
            int hull = 0;
            // Euler: 2n − 2 − h triangles for points in general position.
            var edges = tin.Edges().ToList();
            var edgeUse = new Dictionary<(int, int), int>();
            for (int t = 0; t < tin.TriangleCount; t++)
            {
                var (a, b, c) = tin.Triangle(t);
                foreach (var (u, w) in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = (Math.Min(u, w), Math.Max(u, w));
                    edgeUse[key] = edgeUse.TryGetValue(key, out int used) ? used + 1 : 1;
                }
            }
            Assert.All(edgeUse.Values, used => Assert.InRange(used, 1, 2));
            hull = edgeUse.Values.Count(used => used == 1);
            Assert.Equal(2 * count - 2 - hull, tin.TriangleCount);
            Assert.Equal(edgeUse.Count, edges.Count);
        }

        [Fact]
        public void RegularGridWithCocircularPointsIsStillValid()
        {
            var points = new List<XY>();
            for (int i = 0; i < 12; i++)
            for (int j = 0; j < 9; j++)
                points.Add(new XY(i * 20.0, j * 20.0));
            var tin = Tin.Build(points, points.Select(p => p.X * 0.01).ToList());
            AssertValid(tin, points, true);
            Assert.Equal(2 * 11 * 8, tin.TriangleCount);
        }

        [Fact]
        public void CollinearStartIsHandled()
        {
            var points = new List<XY> { new XY(0, 0), new XY(0, 1), new XY(0, 2), new XY(0, 3), new XY(1, 1.5), new XY(2, 0), new XY(2, 3) };
            var tin = Tin.Build(points, points.Select(_ => 0.0).ToList());
            AssertValid(tin, points, true);
        }

        [Fact]
        public void AllCollinearGiveNoTriangles()
        {
            var points = Enumerable.Range(0, 10).Select(i => new XY(i, 2 * i)).ToList();
            var tin = Tin.Build(points, points.Select(_ => 0.0).ToList());
            Assert.Equal(0, tin.TriangleCount);
            Assert.Null(tin.ZAt(new XY(1, 2)));
        }

        [Fact]
        public void ExactDuplicatesAreSkippedAndReported()
        {
            var points = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(0, 10), new XY(10, 10), new XY(10, 0) };
            var tin = Tin.Build(points, new[] { 1.0, 2, 3, 4, 5 });
            Assert.Single(tin.Duplicates);
            Assert.Equal(2, tin.TriangleCount);
        }

        [Fact]
        public void PlaneIsInterpolatedExactlyAndNothingOutside()
        {
            var random = new Random(7);
            var points = Enumerable.Range(0, 200).Select(_ => new XY(random.NextDouble() * 100, random.NextDouble() * 100)).ToList();
            Func<XY, double> plane = p => 150 + 0.012 * p.X - 0.007 * p.Y;
            var tin = Tin.Build(points, points.Select(plane).ToList());
            for (int i = 0; i < 300; i++)
            {
                var q = new XY(20 + random.NextDouble() * 60, 20 + random.NextDouble() * 60);
                var value = tin.ZAt(q);
                Assert.True(value.HasValue);
                Assert.Equal(plane(q), value.Value, 9);
            }
            Assert.Null(tin.ZAt(new XY(-5, 50)));
            Assert.Null(tin.ZAt(new XY(50, 150)));
            // At a vertex the value is the point elevation.
            Assert.Equal(plane(points[0]), tin.ZAt(points[0]).Value, 9);
        }

        [Fact]
        public void BoundaryIsAnExactMask()
        {
            var points = new List<XY>();
            for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++)
                points.Add(new XY(i * 10.0, j * 10.0));
            // The outline cuts triangles: inside it the surface is interpolated, outside — no data.
            var boundary = new List<XY> { new XY(0, 0), new XY(55, 0), new XY(55, 45), new XY(0, 45) };
            var tin = Tin.Build(points, points.Select(p => p.X * 0.01).ToList(), null, boundary);
            Assert.Equal(200, tin.TriangleCount);
            Assert.Equal(0.25, tin.ZAt(new XY(25, 25)).Value, 9);
            Assert.Equal(0.54, tin.ZAt(new XY(54, 44)).Value, 9);
            Assert.Equal(0.55, tin.ZAt(new XY(55, 20)).Value, 9); // on the outline
            Assert.Null(tin.ZAt(new XY(56, 20)));
            Assert.Null(tin.ZAt(new XY(75, 75)));
            // Edges drawn only inside the outline.
            foreach (var (a, b, _) in tin.DisplayEdges())
            {
                Assert.True(Geometry.Contains(boundary, a, 1e-6));
                Assert.True(Geometry.Contains(boundary, b, 1e-6));
            }
        }

        [Fact]
        public void TwentyThousandPointsBuildQuickly()
        {
            var random = new Random(11);
            var points = Enumerable.Range(0, 20_000)
                .Select(_ => new XY(5_500_000 + random.NextDouble() * 2000, 4_300_000 + random.NextDouble() * 2000)).ToList();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var tin = Tin.Build(points, points.Select(_ => 100.0).ToList());
            watch.Stop();
            Assert.True(tin.TriangleCount > 39_000);
            Assert.True(watch.Elapsed.TotalSeconds < 10, "built in " + watch.Elapsed);
            AssertValid(tin, points, false);
        }
    }

    public class GeometryTests
    {
        [Fact]
        public void ClipSquareByTriangleWindow()
        {
            var square = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(10, 10), new XY(0, 10) };
            var triangle = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(0, 10) };
            Assert.Equal(50, Geometry.Area(Geometry.ClipByConvex(square, triangle)), 9);
        }

        [Fact]
        public void EarClippingOfConcavePolygon()
        {
            var l = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(10, 4), new XY(4, 4), new XY(4, 10), new XY(0, 10) };
            var triangles = Geometry.Triangulate(l);
            Assert.NotNull(triangles);
            Assert.Equal(4, triangles.Count);
            Assert.Equal(Geometry.Area(l), triangles.Sum(t => Geometry.Area(t)), 9);
        }

        [Fact]
        public void SegmentInsideConcavePolygon()
        {
            var u = new List<XY> { new XY(0, 0), new XY(30, 0), new XY(30, 30), new XY(20, 30), new XY(20, 10), new XY(10, 10), new XY(10, 30), new XY(0, 30) };
            var parts = Geometry.ClipSegment(new XY(-5, 20), new XY(35, 20), u);
            Assert.Equal(2, parts.Count);
            Assert.Equal(20, parts.Sum(p => p.A.DistanceTo(p.B)), 9);
        }
    }
}
