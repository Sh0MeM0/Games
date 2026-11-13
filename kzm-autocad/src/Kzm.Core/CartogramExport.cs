using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>Что выгружается (задание п. 6): узлы, квадраты, итоги и контрольный расчёт.</summary>
    public sealed class CartogramReport
    {
        public CartogramInfo Info { get; set; }
        public CartogramResult Result { get; set; }
        public string FactName { get; set; }
        public string ProjectName { get; set; }
        public ControlVolumeResult Control { get; set; }
        public int ElevationDecimals { get; set; } = KzmSettings.DefaultDecimals;
        public IReadOnlyList<string> Problems { get; set; } = new string[0];
        public string Created { get; set; }
    }

    public static class CartogramExport
    {
        public static string ColumnLabel(CartogramInfo c, CartogramResult r, int i) => GridLabels.Label(c.Columns, i - r.IMin + 1);

        /// <summary>Rows are counted from the top (as on the drawing).</summary>
        public static string RowLabel(CartogramInfo c, CartogramResult r, int j) => GridLabels.Label(c.Rows, r.JMax - j + 1);

        public static List<ExportSheet> Sheets(CartogramReport report)
        {
            var c = report.Info;
            var r = report.Result;
            int hd = Math.Max(2, report.ElevationDecimals);
            var sheets = new List<ExportSheet>();

            var nodes = new ExportSheet("Узлы") { FrozenRows = 1 };
            nodes.Header("Линия сетки слева направо", "Линия сетки сверху вниз", "X, м", "Y, м", "Hф, м", "Hпр, м", "h = Hпр − Hф, м", "Примечание");
            foreach (var node in r.Nodes.Values.Where(n => n.Used).OrderByDescending(n => n.J).ThenBy(n => n.I))
            {
                nodes.Row(node.I - r.IMin + 1, r.JMax + 1 - node.J + 1, node.Position.X, node.Position.Y,
                    (object)node.Fact ?? string.Empty, (object)node.Project ?? string.Empty, (object)node.Working ?? string.Empty,
                    node.Working.HasValue ? string.Empty : "нет данных поверхности");
            }
            foreach (var (column, decimals) in new[] { (2, 3), (3, 3), (4, hd), (5, hd), (6, hd) }) nodes.Decimals[column] = decimals;
            foreach (var (column, width) in new[] { (0, 12.0), (1, 12.0), (2, 14.0), (3, 14.0), (4, 10.0), (5, 10.0), (6, 12.0), (7, 26.0) }) nodes.Widths[column] = width;
            sheets.Add(nodes);

            var squares = new ExportSheet("Квадраты") { FrozenRows = 1 };
            squares.Header("Столбец", "Ряд", "Площадь, м²", "Насыпь (+), м³", "Выемка (–), м³", "Квадрат", "Примечание");
            foreach (var square in r.Squares.OrderByDescending(s => s.J).ThenBy(s => s.I))
            {
                squares.Row(ColumnLabel(c, r, square.I), RowLabel(c, r, square.J), square.Area,
                    square.Computed ? (object)square.Fill : string.Empty, square.Computed ? (object)square.Cut : string.Empty,
                    square.Full ? "полный" : "неполный (по границе)", square.Problem ?? string.Empty);
            }
            foreach (var column in new[] { 2, 3, 4 }) squares.Decimals[column] = 2;
            foreach (var (column, width) in new[] { (0, 9.0), (1, 9.0), (2, 13.0), (3, 15.0), (4, 15.0), (5, 22.0), (6, 40.0) }) squares.Widths[column] = width;
            sheets.Add(squares);

            var totals = new ExportSheet("Итоги");
            totals.Header("Итоги по столбцам", "Насыпь (+), м³", "Выемка (–), м³");
            foreach (var i in r.ColumnTotals.Keys.OrderBy(k => k))
                totals.Row(ColumnLabel(c, r, i), r.ColumnTotals[i].Fill, r.ColumnTotals[i].Cut);
            totals.Row();
            totals.Header("Итоги по рядам", "Насыпь (+), м³", "Выемка (–), м³");
            foreach (var j in r.RowTotals.Keys.OrderByDescending(k => k))
                totals.Row(RowLabel(c, r, j), r.RowTotals[j].Fill, r.RowTotals[j].Cut);
            totals.Row();
            totals.Header("Всего", "Насыпь (+), м³", "Выемка (–), м³");
            totals.Row("По картограмме (метод квадратов)", r.TotalFill, r.TotalCut);
            if (report.Control != null)
            {
                totals.Row("Контрольный расчёт (наложение поверхностей)", report.Control.Fill, report.Control.Cut);
                var fill = ControlVolumeResult.Difference(r.TotalFill, report.Control.Fill);
                var cut = ControlVolumeResult.Difference(r.TotalCut, report.Control.Cut);
                totals.Row("Расхождение, м³", fill.Cubic, cut.Cubic);
                totals.Row("Расхождение, %", (object)fill.Percent ?? "—", (object)cut.Percent ?? "—");
            }
            totals.Row();
            totals.Header("Показатель", "Значение");
            totals.Row("Баланс: насыпь − выемка, м³ (+ недостаток грунта, − избыток)", r.TotalFill - r.TotalCut);
            totals.Row("Площадь в границе работ, м²", r.BoundaryArea);
            totals.Row("Рассчитано, м²", r.ComputedArea);
            if (report.Control != null && report.Control.MissingArea > 0)
                totals.Row("Нет данных поверхностей (контрольный расчёт), м²", report.Control.MissingArea);
            totals.Row("Квадратов всего / полных / неполных", r.Squares.Count + " / " + r.Squares.Count(s => s.Full) + " / " + r.Squares.Count(s => !s.Full));
            totals.Row("Проверка: Σ по столбцам = Σ по рядам = итог", r.Balanced ? "сходится" : "НЕ СХОДИТСЯ");
            totals.Row();
            totals.Header("Параметры", string.Empty);
            totals.Row("Картограмма", c.Name);
            totals.Row("Фактическая поверхность", report.FactName ?? string.Empty);
            totals.Row("Проектная поверхность", report.ProjectName ?? string.Empty);
            totals.Row("Шаг сетки, м", c.Step);
            totals.Row("Угол поворота сетки, °", c.AngleDegrees);
            totals.Row("Базовая точка (узел 0,0), X; Y", r.Grid.Origin.X.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',') + "; " +
                                                       r.Grid.Origin.Y.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ','));
            totals.Row("Масштаб", "1:" + c.Scale.ToString(CultureInfo.InvariantCulture));
            totals.Row("Сформировано", report.Created ?? string.Empty);
            if (report.Problems.Count > 0)
            {
                totals.Row();
                totals.Header("Замечания", string.Empty);
                foreach (var problem in report.Problems) totals.Row(problem);
            }
            foreach (var column in new[] { 1, 2 }) totals.Decimals[column] = 2;
            totals.Widths[0] = 62;
            totals.Widths[1] = 18;
            totals.Widths[2] = 18;
            sheets.Add(totals);
            return sheets;
        }
    }
}
