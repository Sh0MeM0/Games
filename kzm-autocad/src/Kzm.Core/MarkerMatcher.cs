using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    public sealed class MarkerMatch
    {
        internal MarkerMatch(int markerIndex, double distance, ImportReason failure, string message)
        {
            MarkerIndex = markerIndex;
            Distance = distance;
            Failure = failure;
            Message = message;
        }

        /// <summary>Index into the marker list, or -1.</summary>
        public int MarkerIndex { get; }
        public double Distance { get; }
        public ImportReason Failure { get; }
        public string Message { get; }
        public bool Success => MarkerIndex >= 0 && Failure == ImportReason.None;
    }

    /// <summary>
    /// Способ (б) без радиуса поиска: текст и маркер связываются, только если они
    /// взаимно ближайшие — ближайший к тексту маркер, а для этого маркера ближайший
    /// текст — этот же. Остальное не решается молча и становится спорным:
    /// маркеров нет; два маркера на одинаковом расстоянии (разница не больше допуска
    /// совпадения); ближайший маркер ближе к другой отметке или равноудалён от двух.
    /// </summary>
    public static class MarkerMatcher
    {
        public static IReadOnlyList<MarkerMatch> Match(IReadOnlyList<XY> anchors, IReadOnlyList<XY> markers, double tieTolerance)
        {
            Guard.List(anchors, nameof(anchors));
            Guard.List(markers, nameof(markers));
            if (!Guard.Finite(tieTolerance) || tieTolerance < 0) throw new KzmException("Допуск совпадения задан неверно.");

            var result = new MarkerMatch[anchors.Count];
            if (markers.Count == 0)
            {
                for (int i = 0; i < anchors.Count; i++)
                    result[i] = new MarkerMatch(-1, double.NaN, ImportReason.NoMarker, "маркеры не найдены");
                return result;
            }
            var markerIndex = new NearestIndex(markers);
            var textIndex = new NearestIndex(anchors);
            for (int i = 0; i < anchors.Count; i++)
            {
                var (m1, d1, m2, d2) = markerIndex.TwoNearest(anchors[i]);
                if (m2 >= 0 && d2 - d1 <= tieTolerance)
                {
                    result[i] = new MarkerMatch(-1, d1, ImportReason.MarkerTie,
                        "два маркера на одинаковом расстоянии (" + ElevationFormat.Format(d1, 3) + " и " + ElevationFormat.Format(d2, 3) + " м)");
                    continue;
                }
                var (t1, e1, t2, e2) = textIndex.TwoNearest(markers[m1]);
                if (t1 != i && !(t2 == i && e2 - e1 <= tieTolerance))
                {
                    result[i] = new MarkerMatch(-1, d1, ImportReason.MarkerShared,
                        "ближайший маркер (" + ElevationFormat.Format(d1, 2) + " м) ближе к другой отметке");
                    continue;
                }
                if (t2 >= 0 && e2 - e1 <= tieTolerance)
                {
                    result[i] = new MarkerMatch(-1, d1, ImportReason.MarkerShared,
                        "ближайший маркер на одинаковом расстоянии от двух отметок");
                    continue;
                }
                result[i] = new MarkerMatch(m1, d1, ImportReason.None, string.Empty);
            }
            return result;
        }
    }

    /// <summary>Nearest-neighbour search on a uniform grid (no search radius needed).</summary>
    internal sealed class NearestIndex
    {
        private readonly IReadOnlyList<XY> points;
        private readonly double minX, minY, cell;
        private readonly int nx, ny;
        private readonly List<int>[] cells;

        internal NearestIndex(IReadOnlyList<XY> points)
        {
            this.points = points;
            if (points.Count == 0) return;
            minX = points.Min(p => p.X);
            minY = points.Min(p => p.Y);
            double width = Math.Max(points.Max(p => p.X) - minX, 1e-6);
            double height = Math.Max(points.Max(p => p.Y) - minY, 1e-6);
            // About one point per cell; points on one line must not give hair-thin cells;
            // at most 2048 cells per side.
            double longest = Math.Max(width, height);
            cell = Math.Max(Math.Sqrt(width * height / points.Count), longest / points.Count);
            cell = Math.Max(cell, longest / 2048);
            nx = (int)(width / cell) + 1;
            ny = (int)(height / cell) + 1;
            cells = new List<int>[nx * ny];
            for (int i = 0; i < points.Count; i++)
            {
                int key = Clamp((int)Math.Floor((points[i].X - minX) / cell), nx) * ny + Clamp((int)Math.Floor((points[i].Y - minY) / cell), ny);
                (cells[key] ?? (cells[key] = new List<int>())).Add(i);
            }
        }

        private static int Clamp(int value, int count) => value < 0 ? 0 : value >= count ? count - 1 : value;

        /// <summary>The two nearest points (index -1 / +∞ when there are fewer).</summary>
        internal (int First, double FirstDistance, int Second, double SecondDistance) TwoNearest(XY query)
        {
            int first = -1, second = -1;
            double d1 = double.PositiveInfinity, d2 = double.PositiveInfinity;
            if (points.Count == 0) return (first, d1, second, d2);
            int cx = Clamp((int)Math.Floor((query.X - minX) / cell), nx);
            int cy = Clamp((int)Math.Floor((query.Y - minY) / cell), ny);
            int maxRing = Math.Max(Math.Max(cx, nx - 1 - cx), Math.Max(cy, ny - 1 - cy));
            for (int ring = 0; ring <= maxRing; ring++)
            {
                // Only the perimeter of the ring: cells with max(|dx|, |dy|) == ring.
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    if (x < 0 || x >= nx) continue;
                    bool edgeColumn = x == cx - ring || x == cx + ring;
                    int step = edgeColumn || ring == 0 ? 1 : 2 * ring;
                    for (int y = cy - ring; y <= cy + ring; y += step)
                    {
                        if (y < 0 || y >= ny) continue;
                        var list = cells[x * ny + y];
                        if (list == null) continue;
                        foreach (int index in list)
                        {
                            double d = points[index].DistanceTo(query);
                            if (d < d1 || (d == d1 && index < first)) { second = first; d2 = d1; first = index; d1 = d; }
                            else if (d < d2) { second = index; d2 = d; }
                        }
                    }
                }
                // Cells of the next rings are at least ring×cell away from the query.
                if (second >= 0 && d2 <= ring * cell) break;
            }
            return (first, d1, second, d2);
        }
    }
}
