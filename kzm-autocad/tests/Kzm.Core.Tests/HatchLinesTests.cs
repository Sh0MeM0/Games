using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 33: штриховка выемки своими линиями под 45° с точным шагом.</summary>
    public class HatchLinesTests
    {
        private static readonly double Root2 = Math.Sqrt(2);

        // Квадрат 20 × 20, шаг 2 м: линии x − y = c через каждые 2·√2 по X; все под 45°, расстояние между соседними — 2,00.
        [Fact]
        public void LinesAre45DegreesAndExactlySpaced()
        {
            var square = new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 20), new XY(0, 20) };
            var lines = HatchLines.Build(new[] { square }, 2, new XY(0, 0));
            Assert.NotEmpty(lines);
            foreach (var (a, b) in lines)
            {
                Assert.Equal(1, (b.Y - a.Y) / (b.X - a.X), 9);              // 45°
                Assert.True(a.X >= -1e-9 && a.X <= 20 + 1e-9 && a.Y >= -1e-9 && a.Y <= 20 + 1e-9);
            }
            // Перпендикулярное расстояние между соседними линиями: |(y − x)| / √2 — кратно 2,00.
            var offsets = lines.Select(l => (l.A.Y - l.A.X) / Root2).Distinct(new ToleranceComparer(1e-6)).OrderBy(v => v).ToList();
            for (int i = 1; i < offsets.Count; i++) Assert.Equal(2.0, offsets[i] - offsets[i - 1], 6);
            // Через квадрат проходит 20·√2 / 2 ≈ 14,1 линии: от −14 до 14 включительно, кроме проходящих только через угол.
            Assert.InRange(lines.Count, 13, 15);
        }

        // Два соседних куска выемки: одна и та же линия в обоих — один отрезок, без разрыва на общей стороне.
        [Fact]
        public void LinesContinueAcrossNeighbouringPieces()
        {
            var left = new List<XY> { new XY(0, 0), new XY(10, 0), new XY(10, 10), new XY(0, 10) };
            var right = new List<XY> { new XY(10, 0), new XY(20, 0), new XY(20, 10), new XY(10, 10) };
            var both = HatchLines.Build(new[] { left, right }, 2, new XY(0, 0));
            var whole = HatchLines.Build(new[] { new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 10), new XY(0, 10) } }, 2, new XY(0, 0));
            Assert.Equal(whole.Count, both.Count);
            double Length(IEnumerable<(XY A, XY B)> set) => set.Sum(l => l.A.DistanceTo(l.B));
            Assert.Equal(Length(whole), Length(both), 6);
        }

        // Координаты съёмки (~5 000 000 м): шаг не теряется.
        [Fact]
        public void SurveyCoordinates()
        {
            double x0 = 3251280, y0 = 5953020;
            var square = new List<XY> { new XY(x0, y0), new XY(x0 + 10, y0), new XY(x0 + 10, y0 + 10), new XY(x0, y0 + 10) };
            var lines = HatchLines.Build(new[] { square }, 4, new XY(3251200, 5953000));
            var offsets = lines.Select(l => ((l.A.Y - 5953000) - (l.A.X - 3251200)) / Root2).OrderBy(v => v).ToList();
            for (int i = 1; i < offsets.Count; i++) Assert.Equal(4.0, offsets[i] - offsets[i - 1], 6);
        }

        // Порог мелкой выемки — квадрат со стороной в два шага: 16 м² при шаге 2 м (4 мм при 1:500), 64 м² при 4 м (1:1000).
        [Theory]
        [InlineData(2, 16)]
        [InlineData(4, 64)]
        public void MinimumArea(double spacing, double area) => Assert.Equal(area, HatchRegions.MinimumArea(spacing), 9);
    }
}
