using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 26: мелкая выемка не штрихуется, а куски большой — штрихуются все.</summary>
    public class HatchRegionsTests
    {
        private static XY[] Tri(double x, double y, double size) => new[] { new XY(x, y), new XY(x + size, y), new XY(x, y + size) };

        [Fact]
        public void IsolatedSmallPieceIsSkipped()
        {
            // Шаг штриховки 2 м (4 мм при 1:500): порог 2 × 2 = 4 м². Треугольник 1,5 × 1,5 / 2 = 1,125 м² — без штриховки.
            var polygons = new List<IReadOnlyList<XY>> { Tri(0, 0, 1.5), Tri(100, 100, 10) };
            var kept = HatchRegions.Kept(polygons, 4);
            Assert.Equal(new[] { 1 }, kept.OrderBy(i => i).ToArray());
        }

        [Fact]
        public void SmallPieceOfALargeRegionIsKept()
        {
            // Квадрат 20 × 20 выемки и узкий кусок 0,5 × 20 в соседнем квадрате, касается его стороной.
            var big = new[] { new XY(0, 0), new XY(20, 0), new XY(20, 20), new XY(0, 20) };
            var sliver = new[] { new XY(20, 0), new XY(20.5, 0), new XY(20.5, 3), new XY(20, 3) };
            var kept = HatchRegions.Kept(new List<IReadOnlyList<XY>> { big, sliver }, 4);
            Assert.Equal(2, kept.Count);
        }

        [Fact]
        public void PiecesTouchingAtACornerFormOneRegion()
        {
            // Два куска по 2,0 м² касаются в точке: вместе 4,0 м² — штрихуются оба.
            var a = Tri(0, 0, 2);
            var b = new[] { new XY(2, 0), new XY(4, 0), new XY(4, 2) };
            Assert.Equal(2, HatchRegions.Kept(new List<IReadOnlyList<XY>> { a, b }, 4).Count);
            Assert.Empty(HatchRegions.Kept(new List<IReadOnlyList<XY>> { a }, 4));
        }
    }
}
