using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kzm.Core
{
    /// <summary>
    /// Текст с площадями рядом с объёмами (замечание 23), в виде образца пользователя:
    /// «Общая площадь насыпи = 8439 м2» и т. д. Площади — по тем же частям квадратов, что и объёмы
    /// (квадраты, разделённые линией нулевых работ); округление до 1 м².
    /// </summary>
    public static class CartogramAreas
    {
        public static string Square(double value) =>
            Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " м2";

        /// <summary>
        /// Строки текста: насыпь, выемка, 0-область, вся картограмма (площадь в границе работ). Если часть квадратов
        /// не рассчитана (нет данных поверхностей), добавляется строка с этой площадью — чтобы сумма сходилась.
        /// </summary>
        public static List<string> Lines(CartogramResult result)
        {
            var lines = new List<string>
            {
                "Общая площадь насыпи = " + Square(result.FillArea),
                "Общая площадь выемки = " + Square(result.CutArea),
                "Общая площадь 0-области = " + Square(result.ZeroArea),
                "Общая площадь картограммы = " + Square(result.BoundaryArea),
            };
            double missing = result.BoundaryArea - result.ComputedArea;
            if (missing > 1e-6 * Math.Max(1, result.BoundaryArea))
                lines.Add("Не рассчитано (нет данных поверхностей) = " + Square(missing));
            return lines;
        }
    }
}
