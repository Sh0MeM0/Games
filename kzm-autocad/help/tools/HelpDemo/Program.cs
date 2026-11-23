using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kzm.Core;

// Пример для картинок справки: та же расчётная часть, что в модуле (метод квадратов, штриховка, площади).
// Запуск: dotnet run -- <файл.json>
internal static class Program
{
    private static object P(XY p) => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4) };

    private static object Cartogram(IReadOnlyList<IReadOnlyList<XY>> parts, Func<XY, double?> fact, Func<XY, double?> project, double step, int scale, double hatchMm)
    {
        var grid = new GridDefinition(step, new XY(0, 0), 0);
        var r = CartogramCalculator.Compute(parts, fact, project, grid);
        double k = scale / 1000.0;
        double spacing = hatchMm * k;
        var pieces = r.Squares.Where(s => s.Computed).SelectMany(s => s.CutPolygons).Select(p => (IReadOnlyList<XY>)p).ToList();
        var kept = HatchRegions.Kept(pieces, HatchRegions.MinimumArea(spacing));
        var hatch = HatchLines.Build(pieces.Where((p, i) => kept.Contains(i)), spacing, grid.Origin);
        return new
        {
            step, scale,
            boundaries = parts.Select(b => b.Select(P).ToList()).ToList(),
            grid = r.GridSegments.Select(s => new[] { P(s.A), P(s.B) }).ToList(),
            range = new[] { r.IMin, r.IMax, r.JMin, r.JMax },
            nodes = r.Nodes.Values.Where(n => n.Used && n.Inside && n.Working.HasValue)
                .Select(n => new { at = P(n.Position), fact = n.Fact.Value, project = n.Project.Value, working = n.Working.Value }).ToList(),
            boundaryPoints = r.BoundaryPoints.Where(b => b.Working.HasValue)
                .Select(b => new { at = P(b.Position), fact = b.Fact.Value, project = b.Project.Value, working = b.Working.Value }).ToList(),
            squares = r.Squares.Where(s => s.Computed).Select(s => new { i = s.I, j = s.J, fill = s.Fill, cut = s.Cut, at = P(s.LabelPoint) }).ToList(),
            zero = r.ZeroLines.Select(l => l.Select(P).ToList()).ToList(),
            hatch = hatch.Select(s => new[] { P(s.A), P(s.B) }).ToList(),
            columns = r.ColumnTotals.OrderBy(c => c.Key).Select(c => new { i = c.Key, fill = c.Value.Fill, cut = c.Value.Cut }).ToList(),
            totalFill = r.TotalFill, totalCut = r.TotalCut,
            areas = CartogramAreas.Lines(r),
            skipped = pieces.Count - kept.Count,
        };
    }

    private static int Main(string[] args)
    {
        // Площадка неправильной формы; рельеф понижается к востоку, проект — горизонтальная площадка 150,90:
        // на западе насыпь, на востоке выемка, между ними линия нулевых работ.
        var site = new List<XY> { new XY(3, 4), new XY(85, 2), new XY(94, 35), new XY(50, 38.5), new XY(6, 37) };
        Func<XY, double?> fact = p => 150.0 + 0.026 * p.X - 0.006 * p.Y + 0.12 * Math.Sin(p.X / 13.0) * Math.Cos(p.Y / 17.0);
        Func<XY, double?> project = p => 150.90;
        var main = Cartogram(new[] { site }, fact, project, 20, 500, 4);
        // Две части границы — одна общая картограмма (одна сетка, одна таблица).
        var a = new List<XY> { new XY(2, 3), new XY(36, 3), new XY(36, 37), new XY(2, 37) };
        var b = new List<XY> { new XY(52, 8), new XY(78, 8), new XY(78, 33), new XY(52, 33) };
        var parts = Cartogram(new IReadOnlyList<XY>[] { a, b }, fact, project, 20, 500, 4);
        string json = JsonSerializer.Serialize(new { main, parts }, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(args.Length > 0 ? args[0] : "demo.json", json);
        Console.WriteLine("ok");
        return 0;
    }
}
