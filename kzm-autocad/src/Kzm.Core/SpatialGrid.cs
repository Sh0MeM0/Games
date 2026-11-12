using System;
using System.Collections.Generic;

namespace Kzm.Core
{
    /// <summary>Uniform hash grid for neighbour queries in plan.</summary>
    internal sealed class SpatialGrid
    {
        private readonly double cell;
        private readonly Dictionary<(long, long), List<int>> cells = new Dictionary<(long, long), List<int>>();
        private readonly List<XY> points = new List<XY>();

        internal SpatialGrid(double cellSize)
        {
            Guard.Positive(cellSize, "Размер ячейки");
            cell = cellSize;
        }

        internal int Add(XY point)
        {
            int index = points.Count;
            points.Add(point);
            var key = (Cell(point.X), Cell(point.Y));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(index);
            return index;
        }

        internal XY this[int index] => points[index];

        /// <summary>Indices of points with distance ≤ radius (exact check).</summary>
        internal IEnumerable<int> Within(XY center, double radius)
        {
            long x0 = Cell(center.X - radius), x1 = Cell(center.X + radius);
            long y0 = Cell(center.Y - radius), y1 = Cell(center.Y + radius);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 4_000_000)
                throw new KzmException("Радиус поиска слишком велик по сравнению с размером ячейки.");
            for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            {
                if (!cells.TryGetValue((x, y), out var list)) continue;
                foreach (int index in list)
                    if (points[index].DistanceTo(center) <= radius) yield return index;
            }
        }

        private long Cell(double value)
        {
            double scaled = Math.Floor(value / cell);
            if (!Guard.Finite(scaled) || Math.Abs(scaled) > 1e15)
                throw new KzmException("Координаты слишком велики для выбранного допуска.");
            return (long)scaled;
        }
    }
}
