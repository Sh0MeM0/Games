using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Площади насыпи, выемки и «0-области» (замечание 23, версия 0.8).</summary>
    public class AreaTests
    {
        private static readonly List<XY> Square = new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 20), new XY(0, 20) };
        private static GridDefinition Grid() => new GridDefinition(20, new XY(0, 0), 0);

        private static Func<XY, double> Bilinear(double h00, double h10, double h11, double h01) => p =>
        {
            double u = p.X / 20, v = p.Y / 20;
            return h00 * (1 - u) * (1 - v) + h10 * u * (1 - v) + h11 * u * v + h01 * (1 - u) * v;
        };

        // Т1: вся площадь 20×20 — насыпь.
        [Fact]
        public void FillOnly_WholeSquareIsFill()
        {
            var r = CartogramCalculator.Compute(Square, p => 100.00, p => 100.50, Grid());
            Assert.Equal(400, r.FillArea, 9);
            Assert.Equal(0, r.CutArea, 9);
            Assert.Equal(0, r.ZeroArea, 9);
            Assert.Equal(new[]
            {
                "Общая площадь насыпи = 400 м2",
                "Общая площадь выемки = 0 м2",
                "Общая площадь 0-области = 0 м2",
                "Общая площадь картограммы = 400 м2",
            }, CartogramAreas.Lines(r));
        }

        // Т3: линия нулевых работ по середине квадрата — 200 м² насыпи и 200 м² выемки.
        [Fact]
        public void ZeroLineThroughMiddle_HalfAndHalf()
        {
            var project = Bilinear(0.5, 0.5, -0.5, -0.5);
            var r = CartogramCalculator.Compute(Square, p => 120.0, p => 120.0 + project(p), Grid());
            Assert.Equal(200, r.FillArea, 9);
            Assert.Equal(200, r.CutArea, 9);
            Assert.Equal(0, r.ZeroArea, 9);
        }

        // Проект совпадает с фактом: вся площадь — «0-область».
        [Fact]
        public void SameSurfaces_AllZeroArea()
        {
            var r = CartogramCalculator.Compute(Square, p => 100.00, p => 100.00, Grid());
            Assert.Equal(0, r.FillArea, 9);
            Assert.Equal(0, r.CutArea, 9);
            Assert.Equal(400, r.ZeroArea, 9);
            Assert.Equal("Общая площадь 0-области = 400 м2", CartogramAreas.Lines(r)[2]);
        }

        // Два квадрата: в левом рабочие отметки 0 (0-область 400 м²), в правом насыпь (400 м²); всего 800 м².
        [Fact]
        public void ZeroSquareAndFillSquare()
        {
            var boundary = new List<XY> { new XY(0, 0), new XY(40, 0), new XY(40, 20), new XY(0, 20) };
            var r = CartogramCalculator.Compute(boundary, p => 100.0, p => 100.0 + (p.X > 20 ? (p.X - 20) * 0.025 : 0), Grid());
            Assert.Equal(400, r.FillArea, 9);
            Assert.Equal(0, r.CutArea, 9);
            Assert.Equal(400, r.ZeroArea, 9);
            Assert.Equal(new[]
            {
                "Общая площадь насыпи = 400 м2",
                "Общая площадь выемки = 0 м2",
                "Общая площадь 0-области = 400 м2",
                "Общая площадь картограммы = 800 м2",
            }, CartogramAreas.Lines(r));
        }

        // Неровная граница и наклонная плоскость: насыпь + выемка + 0-область = рассчитанная площадь = площадь в границе.
        [Fact]
        public void IrregularBoundary_PartsSumToArea()
        {
            var boundary = new List<XY> { new XY(3, 2), new XY(57, 7), new XY(61, 38), new XY(30, 52), new XY(26, 30), new XY(1, 44) };
            var r = CartogramCalculator.Compute(boundary, p => 50.0 + 0.01 * p.X, p => 50.3 + 0.004 * p.Y, Grid());
            Assert.True(r.FillArea > 0 && r.CutArea > 0);
            Assert.Equal(r.ComputedArea, r.FillArea + r.CutArea + r.ZeroArea, 6);
            Assert.Equal(r.BoundaryArea, r.ComputedArea, 6);
            Assert.Equal(4, CartogramAreas.Lines(r).Count);
        }

        // Квадрат без данных поверхности: его площадь — отдельной строкой, чтобы сумма сходилась.
        [Fact]
        public void MissingData_ExtraLine()
        {
            var boundary = new List<XY> { new XY(0, 0), new XY(40, 0), new XY(40, 20), new XY(0, 20) };
            var r = CartogramCalculator.Compute(boundary, p => p.X > 20.5 ? (double?)null : 100.0, p => 100.5, Grid());
            var lines = CartogramAreas.Lines(r);
            Assert.Equal(5, lines.Count);
            Assert.Equal("Общая площадь картограммы = 800 м2", lines[3]);
            Assert.Equal("Не рассчитано (нет данных поверхностей) = 400 м2", lines[4]);
            Assert.Equal(400, r.FillArea, 9);
        }

        // Числа ведомости как в примере А.6 СТБ 2073-2010: «150 000», «94 480», «12 110», но «7320»; ноль — «—».
        [Theory]
        [InlineData(150000, 0, "150 000")]
        [InlineData(94480, 0, "94 480")]
        [InlineData(12110, 0, "12 110")]
        [InlineData(7320, 0, "7320")]
        [InlineData(0.4, 0, "—")]
        [InlineData(1234567.46, 1, "1 234 567,5")]
        [InlineData(-35800, 0, "-35 800")]
        public void StatementNumbers(double value, int decimals, string expected) =>
            Assert.Equal(expected, ElevationFormat.Grouped(value, decimals));

        // Округление до целых м² — как в образце («8439 м2»).
        [Fact]
        public void Rounding()
        {
            Assert.Equal("8439 м2", CartogramAreas.Square(8439.4999));
            Assert.Equal("502 м2", CartogramAreas.Square(501.5));
            Assert.Equal("0 м2", CartogramAreas.Square(0.2));
        }
    }
}

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 24: рабочая отметка — по отметкам, округлённым до 0,01 м, как они подписаны.</summary>
    public class WorkingMarkTests
    {
        private static readonly List<XY> Square = new List<XY> { new XY(0, 0), new XY(20, 0), new XY(20, 20), new XY(0, 20) };

        [Theory]
        [InlineData(176.374, 176.366, 0.0)]   // подписано 176,37 и 176,37 — рабочая 0,00, а не +0,01
        [InlineData(176.825, 176.70, 0.13)]   // 176,83 − 176,70
        [InlineData(100.004, 100.0, 0.0)]
        [InlineData(100.0, 100.005, -0.01)]   // 100,00 − 100,01
        public void RoundedLikeTheLabels(double project, double fact, double expected) =>
            Assert.Equal(expected, WorkingMark.Of(project, fact), 9);

        // Разность поверхностей в несколько миллиметров подписывается «0,00» — это 0-область, а не насыпь (замечание 24).
        [Fact]
        public void MillimetresAreZeroArea()
        {
            var r = CartogramCalculator.Compute(Square, p => 176.13, p => 176.13 + 0.003 * p.X / 20, new GridDefinition(20, new XY(0, 0), 0));
            Assert.Equal(400, r.ZeroArea, 9);
            Assert.Equal(0, r.FillArea, 9);
            Assert.Equal(0, r.TotalFill, 9);
            Assert.All(r.Nodes.Values, n => Assert.Equal(0, n.Working.Value, 9));
        }
    }
}
