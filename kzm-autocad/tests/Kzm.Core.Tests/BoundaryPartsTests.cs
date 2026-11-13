using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 31: граница работ и граница поверхности из нескольких частей.</summary>
    public class BoundaryPartsTests
    {
        private static List<XY> Rect(double x0, double y0, double x1, double y1) =>
            new List<XY> { new XY(x0, y0), new XY(x1, y0), new XY(x1, y1), new XY(x0, y1) };

        private static GridDefinition Grid() => new GridDefinition(20, new XY(0, 0), 0);

        // Две отдельные площадки 40 × 20 и 20 × 20 (между ними 40 м): одна картограмма, итог — сумма.
        [Fact]
        public void TwoSeparatePartsGiveOneCartogram()
        {
            var a = Rect(0, 0, 40, 20);
            var b = Rect(80, 20, 100, 40);
            Func<XY, double?> fact = p => 100.0, project = p => 100.5;
            var both = CartogramCalculator.Compute(new IReadOnlyList<XY>[] { a, b }, fact, project, Grid());
            var first = CartogramCalculator.Compute(a, fact, project, Grid());
            var second = CartogramCalculator.Compute(b, fact, project, Grid());
            Assert.Equal(first.TotalFill + second.TotalFill, both.TotalFill, 9);
            Assert.Equal(1200 * 0.5, both.TotalFill, 9); // (800 + 400) м² × 0,50 м
            Assert.Equal(1200, both.BoundaryArea, 9);
            Assert.Equal(1200, both.FillArea, 9);
            Assert.Equal(3, both.Squares.Count);
            Assert.Equal(0, both.IMin);
            Assert.Equal(4, both.IMax);
            Assert.True(both.Balanced);
            // Сетка и подписи — только внутри частей.
            Assert.Equal(first.GridSegments.Count + second.GridSegments.Count, both.GridSegments.Count);
            Assert.All(both.Nodes.Values.Where(n => n.Inside), n => Assert.True(n.Position.X <= 40 || n.Position.X >= 80));
        }

        // Части касаются по стороне: как одна площадка.
        [Fact]
        public void TouchingPartsAreAllowed()
        {
            var r = CartogramCalculator.Compute(new IReadOnlyList<XY>[] { Rect(0, 0, 20, 20), Rect(20, 0, 40, 20) }, p => 100.0, p => 100.0 + 0.01 * p.X, Grid());
            var whole = CartogramCalculator.Compute(Rect(0, 0, 40, 20), p => 100.0, p => 100.0 + 0.01 * p.X, Grid());
            Assert.Equal(whole.TotalFill, r.TotalFill, 9);
            Assert.Equal(800, r.BoundaryArea, 9);
        }

        // Перекрытие и вложенность — понятная ошибка, а не двойной объём.
        [Fact]
        public void OverlappingPartsAreRejected()
        {
            var overlap = Assert.Throws<KzmException>(() =>
                CartogramCalculator.Compute(new IReadOnlyList<XY>[] { Rect(0, 0, 10, 10), Rect(5, 0, 15, 10) }, p => 1.0, p => 2.0, Grid()));
            Assert.Contains("перекрываются", overlap.Message);
            Assert.Contains("50,0", overlap.Message);
            var nested = Assert.Throws<KzmException>(() =>
                CartogramCalculator.Compute(new IReadOnlyList<XY>[] { Rect(0, 0, 40, 40), Rect(10, 10, 20, 20) }, p => 1.0, p => 2.0, Grid()));
            Assert.Contains("Границы 1 и 2", nested.Message);
        }

        // Поверхность из двух частей: между частями отметок нет, рёбра — только внутри частей.
        [Fact]
        public void SurfaceWithTwoParts()
        {
            var points = new List<XY>();
            for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 4; j++)
                points.Add(new XY(i * 10.0, j * 10.0));
            var tin = Tin.Build(points, points.Select(p => 100 + p.X * 0.01).ToList(), null,
                new IReadOnlyList<XY>[] { Rect(0, 0, 30, 40), Rect(70, 0, 100, 40) });
            Assert.Equal(100.1, tin.ZAt(new XY(10, 10)).Value, 9);
            Assert.Null(tin.ZAt(new XY(50, 10)));
            Assert.Equal(100.8, tin.ZAt(new XY(80, 10)).Value, 9);
            Assert.All(tin.DisplayEdges(), e => Assert.True(Math.Max(e.A.X, e.B.X) <= 30 + 1e-9 || Math.Min(e.A.X, e.B.X) >= 70 - 1e-9));
            // Контрольный расчёт по двум частям картограммы — сумма частей.
            var flat = Tin.Build(points, points.Select(_ => 100.0).ToList());
            var control = ControlVolume.Compute(new IReadOnlyList<XY>[] { Rect(0, 0, 20, 20), Rect(80, 0, 100, 20) }, flat, tin);
            // h = 0,01·x: ∫ над [0,20]×[0,20] = 0,01·(20²/2)·20 = 40; над [80,100]×[0,20] = 0,01·(100²−80²)/2·20 = 360.
            Assert.Equal(400, control.Fill, 6);
            Assert.Equal(0, control.MissingArea, 9);
        }

        [Fact]
        public void HandlesAreStoredInTheOldField()
        {
            var c = new CartogramInfo { BoundaryHandle = "14891C" };
            Assert.Equal(new[] { "14891C" }, c.BoundaryHandles);
            c.BoundaryHandles = new[] { "14891C", "15A2F0", "14891c" };
            Assert.Equal("14891C;15A2F0", c.BoundaryHandle);
            c.BoundaryHandles = new string[0];
            Assert.Null(c.BoundaryHandle);
            Assert.Contains("не указана граница работ (замкнутая полилиния)", c.Validate());
        }
    }
}
