using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Штриховка выемки своими линиями под 45° к основанию (СТБ 2073-2010, п. 7.1 б) с точно заданным шагом —
    /// без образцов .pat и их масштаба (замечание 33: мелкие штриховки ANSI31 в AutoCAD выходили сплошными).
    /// Все линии — одного семейства от общей опорной точки, поэтому в соседних кусках выемки они продолжают
    /// друг друга, а отрезки одной линии в соседних кусках сливаются в один.
    /// </summary>
    public static class HatchLines
    {
        private static readonly double Half = Math.Sqrt(0.5);

        /// <param name="polygons">Pieces of cut (drawing coordinates).</param>
        /// <param name="spacing">Distance between the lines, drawing units.</param>
        /// <param name="anchor">A point the lines are counted from (the base point of the grid): precision with survey coordinates.</param>
        public static List<(XY A, XY B)> Build(IEnumerable<IReadOnlyList<XY>> polygons, double spacing, XY anchor)
        {
            if (!(spacing > 0)) throw new ArgumentOutOfRangeException(nameof(spacing));
            // Direction of the lines (45°) and their normal.
            double dx = Half, dy = Half, nx = -Half, ny = Half;
            var byLine = new Dictionary<long, List<(double T0, double T1)>>();
            foreach (var polygon in polygons)
            {
                if (polygon == null || polygon.Count < 3) continue;
                var local = polygon.Select(p => new XY(p.X - anchor.X, p.Y - anchor.Y)).ToList();
                double nMin = local.Min(p => p.X * nx + p.Y * ny), nMax = local.Max(p => p.X * nx + p.Y * ny);
                double tMin = local.Min(p => p.X * dx + p.Y * dy), tMax = local.Max(p => p.X * dx + p.Y * dy);
                long k0 = (long)Math.Ceiling(nMin / spacing - 1e-9), k1 = (long)Math.Floor(nMax / spacing + 1e-9);
                for (long k = k0; k <= k1; k++)
                {
                    double c = k * spacing;
                    var a = new XY(nx * c + dx * (tMin - 1), ny * c + dy * (tMin - 1));
                    var b = new XY(nx * c + dx * (tMax + 1), ny * c + dy * (tMax + 1));
                    foreach (var (p, q) in Geometry.ClipSegment(a, b, local))
                    {
                        double t0 = p.X * dx + p.Y * dy, t1 = q.X * dx + q.Y * dy;
                        if (t1 < t0) (t0, t1) = (t1, t0);
                        if (t1 - t0 <= 1e-9) continue;
                        if (!byLine.TryGetValue(k, out var list)) byLine[k] = list = new List<(double, double)>();
                        list.Add((t0, t1));
                    }
                }
            }
            var result = new List<(XY, XY)>();
            foreach (var pair in byLine.OrderBy(p => p.Key))
            {
                double c = pair.Key * spacing;
                XY At(double t) => new XY(anchor.X + nx * c + dx * t, anchor.Y + ny * c + dy * t);
                var pieces = pair.Value.OrderBy(s => s.T0).ToList();
                double start = pieces[0].T0, end = pieces[0].T1;
                foreach (var (t0, t1) in pieces.Skip(1))
                {
                    if (t0 <= end + 1e-6)
                    {
                        end = Math.Max(end, t1);
                        continue;
                    }
                    result.Add((At(start), At(end)));
                    start = t0;
                    end = t1;
                }
                result.Add((At(start), At(end)));
            }
            return result;
        }
    }
}
