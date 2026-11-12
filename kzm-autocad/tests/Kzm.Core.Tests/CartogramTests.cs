using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Приёмочные тесты Т1–Т4 и Т6 из задания.</summary>
    public class CartogramTests
    {
        private static readonly List<XY> Square = new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 20), new XY(0, 20) };
        private static GridDefinition Grid() => new GridDefinition(20, new XY(0, 0), 0);

        // Т1: факт — плоскость 100,00; проект — 100,50; квадрат 20×20 → насыпь 200, выемка 0.
        [Fact]
        public void T1_HalfMetreFillOverOneSquare()
        {
            var result = CartogramCalculator.Compute(Square, p => 100.00, p => 100.50, Grid());
            Assert.Single(result.Squares);
            Assert.Equal(200, result.TotalFill, 9);
            Assert.Equal(0, result.TotalCut, 9);
            Assert.True(result.Balanced);
            Assert.All(result.Nodes.Values, node => Assert.Equal(0.5, node.Working.Value, 9));
        }

        // Т2: рабочие отметки в углах −0,98; −0,28; −0,25; −0,35 → выемка 186 м³.
        [Fact]
        public void T2_CutWithGivenCornerWorkingElevations()
        {
            var project = Bilinear(-0.98, -0.28, -0.25, -0.35);
            var result = CartogramCalculator.Compute(Square, p => 150.0, p => 150.0 + project(p), Grid());
            Assert.Equal(186, result.TotalCut, 9);
            Assert.Equal(0, result.TotalFill, 9);
            Assert.Equal(-0.98, result.Nodes[(0, 0)].Working.Value, 9);
            Assert.Equal(-0.25, result.Nodes[(1, 1)].Working.Value, 9);
        }

        // Т3: два соседних угла +0,50, два других −0,50 → насыпь 50, выемка 50;
        // линия нулевых работ через середины двух других сторон.
        [Fact]
        public void T3_ZeroLineThroughMidpoints()
        {
            var project = Bilinear(0.5, 0.5, -0.5, -0.5); // (0,0)=+, (20,0)=+, (20,20)=−, (0,20)=−
            var result = CartogramCalculator.Compute(Square, p => 120.0, p => 120.0 + project(p), Grid());
            Assert.Equal(50, result.TotalFill, 9);
            Assert.Equal(50, result.TotalCut, 9);
            var line = Assert.Single(result.ZeroLines);
            var ends = new[] { line.First(), line.Last() }.OrderBy(p => p.X).ToList();
            Assert.Equal(0, ends[0].X, 9); Assert.Equal(10, ends[0].Y, 9);
            Assert.Equal(20, ends[1].X, 9); Assert.Equal(10, ends[1].Y, 9);
            Assert.All(line, p => Assert.Equal(10, p.Y, 9));
        }

        // Т4: как Т1, но контур отсекает половину квадрата по диагонали → насыпь 100.
        [Fact]
        public void T4_DiagonalBoundaryHalvesTheSquare()
        {
            var half = new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 20) };
            var result = CartogramCalculator.Compute(half, p => 100.00, p => 100.50, Grid());
            Assert.Equal(100, result.TotalFill, 9);
            Assert.Equal(200, result.BoundaryArea, 9);
            var square = Assert.Single(result.Squares);
            Assert.False(square.Full);
        }

        // Т6: изменение отметки одной точки факта меняет только затронутые узлы; итоги сходятся.
        [Fact]
        public void T6_ChangingOnePointChangesOnlyTouchedNodes()
        {
            var random = new Random(5);
            var points = new List<XY>();
            for (int i = 0; i <= 12; i++)
            for (int j = 0; j <= 12; j++)
                points.Add(new XY(i * 10 + random.NextDouble() * 4 - 2, j * 10 + random.NextDouble() * 4 - 2));
            var z = points.Select(p => 140 + 0.01 * p.X + 0.02 * p.Y + 0.3 * Math.Sin(p.X / 17)).ToList();
            var boundary = new List<XY> { new XY(5, 5), new XY(115, 5), new XY(115, 115), new XY(5, 115) };
            var grid = new GridDefinition(20, new XY(0, 0), 0);
            Func<XY, double?> design = p => 141.0 + 0.005 * p.X;

            var before = Tin.Build(points, z);
            var first = CartogramCalculator.Compute(boundary, before.ZAt, design, grid);
            int changed = 60;
            var z2 = z.ToList();
            z2[changed] += 1.0;
            var after = Tin.Build(points, z2);
            var second = CartogramCalculator.Compute(boundary, after.ZAt, design, grid);

            // Nodes that differ must lie in triangles that use the changed point.
            var touched = new List<XY[]>();
            for (int t = 0; t < after.TriangleCount; t++)
            {
                var (a, b, c) = after.Triangle(t);
                if (a == changed || b == changed || c == changed) touched.Add(new[] { points[a], points[b], points[c] });
            }
            int differing = 0;
            foreach (var key in first.Nodes.Keys)
            {
                double? h1 = first.Nodes[key].Working, h2 = second.Nodes[key].Working;
                if (h1.HasValue && h2.HasValue && Math.Abs(h1.Value - h2.Value) > 1e-9)
                {
                    differing++;
                    var position = first.Nodes[key].Position;
                    Assert.Contains(touched, tri => Geometry.Contains(tri.ToList(), position, 1e-6));
                }
            }
            Assert.InRange(differing, 1, 6);
            Assert.True(first.Balanced);
            Assert.True(second.Balanced);
            Assert.NotEqual(first.TotalCut, second.TotalCut);
        }

        [Fact]
        public void RotatedGridGivesTheSameVolumeForAPlane()
        {
            var polygon = new List<XY> { new XY(3, 7), new XY(97, 2), new XY(110, 64), new XY(41, 88), new XY(0, 55) };
            double area = Geometry.Area(polygon);
            foreach (double degrees in new[] { 0.0, 17.0, 45.0, -30.0 })
            {
                var grid = new GridDefinition(20, new XY(-13, 4), degrees * Math.PI / 180);
                var result = CartogramCalculator.Compute(polygon, p => 100.0, p => 100.35, grid);
                Assert.Equal(area * 0.35, result.TotalFill, 6);
                Assert.Equal(area, result.ComputedArea, 6);
                Assert.True(result.Balanced);
            }
        }

        [Fact]
        public void ConcaveBoundaryWithBoundaryPointsFromSurfaces()
        {
            // An L-shaped boundary; the working elevation is linear, so the exact volume is known.
            var l = new List<XY> { new XY(0, 0), new XY(50, 0), new XY(50, 25), new XY(25, 25), new XY(25, 50), new XY(0, 50) };
            Func<XY, double?> project = p => 100 + 0.01 * p.X - 0.02 * p.Y;
            var result = CartogramCalculator.Compute(l, p => 100.0, project, new GridDefinition(20, new XY(0, 0), 0));
            // ∫∫ (0,01x − 0,02y) dA over the L, split into fill and cut — compare the net value.
            double net = Integral(l, (x, y) => 0.01 * x - 0.02 * y);
            Assert.Equal(net, result.TotalFill - result.TotalCut, 6);
            Assert.True(result.ZeroLines.Count >= 1);
        }

        [Fact]
        public void SquareWithoutSurfaceDataIsReportedAndNotSummed()
        {
            var boundary = new List<XY> { new XY(0, 0), new XY(40, 0), new XY(40, 20), new XY(0, 20) };
            Func<XY, double?> fact = p => p.X <= 25 ? 100.0 : (double?)null;
            var result = CartogramCalculator.Compute(boundary, fact, p => 100.5, new GridDefinition(20, new XY(0, 0), 0));
            Assert.Equal(2, result.Squares.Count);
            Assert.Single(result.Squares, s => !s.Computed);
            Assert.Equal(200, result.TotalFill, 9);
            Assert.NotEmpty(result.Problems);
            Assert.Null(result.Nodes[(2, 0)].Working);
        }

        [Theory]
        [InlineData(LabelScheme.Numbers, 1, "1")]
        [InlineData(LabelScheme.Numbers, 12, "12")]
        [InlineData(LabelScheme.Letters, 1, "А")]
        [InlineData(LabelScheme.Letters, 7, "Ж")]
        [InlineData(LabelScheme.Letters, 8, "И")]
        [InlineData(LabelScheme.Letters, 22, "Я")]
        [InlineData(LabelScheme.Letters, 23, "АА")]
        public void GridLabels(LabelScheme scheme, int ordinal, string expected)
        {
            Assert.Equal(expected, Kzm.Core.GridLabels.Label(scheme, ordinal));
        }

        /// <summary>Bilinear field over the 20×20 square from corner values (0,0), (20,0), (20,20), (0,20).</summary>
        private static Func<XY, double> Bilinear(double h00, double h10, double h11, double h01) => p =>
        {
            double u = p.X / 20, v = p.Y / 20;
            return h00 * (1 - u) * (1 - v) + h10 * u * (1 - v) + h11 * u * v + h01 * (1 - u) * v;
        };

        /// <summary>∫∫ f over a polygon by fine sampling of its triangulation (Gauss points exact for linear f).</summary>
        private static double Integral(List<XY> polygon, Func<double, double, double> f)
        {
            double sum = 0;
            foreach (var t in Geometry.Triangulate(Geometry.Normalize(polygon)))
            {
                var c = new XY((t[0].X + t[1].X + t[2].X) / 3, (t[0].Y + t[1].Y + t[2].Y) / 3);
                sum += Geometry.Area(t) * f(c.X, c.Y);
            }
            return sum;
        }
    }
}
