using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Мелкая выемка не штрихуется (замечание 26, решает модуль сам). Куски выемки, которые касаются друг друга
    /// (по стороне или в точке, в том числе в соседних квадратах), — один участок. Участок, который на листе меньше
    /// квадрата со стороной в шаг штриховки, не штрихуется: в нём поместилась бы от силы одна короткая линия. Куски
    /// большой выемки на краях квадратов штрихуются, чтобы в ней не было разрывов.
    /// </summary>
    public static class HatchRegions
    {
        /// <summary>Indices of the polygons to hatch: those in regions of at least <paramref name="minArea"/>.</summary>
        public static HashSet<int> Kept(IReadOnlyList<IReadOnlyList<XY>> polygons, double minArea, double tolerance = 1e-5)
        {
            int n = polygons.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            var boxes = polygons.Select(p => (MinX: p.Min(q => q.X), MinY: p.Min(q => q.Y), MaxX: p.Max(q => q.X), MaxY: p.Max(q => q.Y))).ToArray();
            // Sweep by the left edge of the boxes: only neighbours in X are compared.
            var order = Enumerable.Range(0, n).OrderBy(i => boxes[i].MinX).ToArray();
            for (int a = 0; a < n; a++)
            {
                int i = order[a];
                for (int b = a + 1; b < n; b++)
                {
                    int j = order[b];
                    if (boxes[j].MinX > boxes[i].MaxX + tolerance) break;
                    if (boxes[j].MinY > boxes[i].MaxY + tolerance || boxes[i].MinY > boxes[j].MaxY + tolerance) continue;
                    if (Find(i) == Find(j) || !Touch(polygons[i], polygons[j], tolerance)) continue;
                    parent[Find(i)] = Find(j);
                }
            }
            var area = new Dictionary<int, double>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                area.TryGetValue(root, out double sum);
                area[root] = sum + Geometry.Area(polygons[i]);
            }
            var kept = new HashSet<int>();
            for (int i = 0; i < n; i++)
                if (area[Find(i)] >= minArea) kept.Add(i);
            return kept;
        }

        /// <summary>A vertex of one polygon lies on the outline of the other (shared side or corner).</summary>
        private static bool Touch(IReadOnlyList<XY> a, IReadOnlyList<XY> b, double tolerance) => Near(a, b, tolerance) || Near(b, a, tolerance);

        private static bool Near(IReadOnlyList<XY> points, IReadOnlyList<XY> outline, double tolerance)
        {
            foreach (var p in points)
                for (int k = 0, m = outline.Count; k < m; k++)
                    if (Geometry.DistanceToSegment(p, outline[k], outline[(k + 1) % m]) <= tolerance) return true;
            return false;
        }
    }
}
