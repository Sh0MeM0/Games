using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>Axis-aligned rectangle of a label on the drawing (m).</summary>
    public struct LabelBox
    {
        public LabelBox(double minX, double minY, double maxX, double maxY)
        {
            MinX = Math.Min(minX, maxX);
            MinY = Math.Min(minY, maxY);
            MaxX = Math.Max(minX, maxX);
            MaxY = Math.Max(minY, maxY);
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }

        public LabelBox Moved(double dx, double dy) => new LabelBox(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);

        public LabelBox Union(LabelBox other) =>
            new LabelBox(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

        public bool Overlaps(LabelBox other, double gap) =>
            MinX < other.MaxX + gap && other.MinX < MaxX + gap && MinY < other.MaxY + gap && other.MinY < MaxY + gap;

        /// <summary>Nearest point of the rectangle to p (the end of a leader).</summary>
        public XY Nearest(XY p) => new XY(Math.Max(MinX, Math.Min(MaxX, p.X)), Math.Max(MinY, Math.Min(MaxY, p.Y)));
    }

    /// <summary>The labels of one point (node or crossing of the grid with the boundary), moved together.</summary>
    public sealed class LabelGroup
    {
        public LabelGroup(XY anchor, LabelBox box)
        {
            Anchor = anchor;
            Box = box;
        }

        public XY Anchor { get; }
        /// <summary>Union of the label rectangles at the default place.</summary>
        public LabelBox Box { get; }
        public double Dx { get; internal set; }
        public double Dy { get; internal set; }
        /// <summary>Moved away from the point: a leader line connects them.</summary>
        public bool Leader { get; internal set; }
        /// <summary>No free place was found: left at the default place over another label.</summary>
        public bool Overlapping { get; internal set; }
        public LabelBox Placed => Box.Moved(Dx, Dy);
    }

    /// <summary>
    /// Читаемость подписей: группы подписей ставятся по порядку; если группа налезает на уже
    /// поставленные подписи (или на неподвижные — объёмы), она отодвигается на ближайшее
    /// свободное место внутри границы, и рисуется выноска от точки к подписи.
    /// </summary>
    public static class LabelLayout
    {
        private const int Directions = 16;

        /// <param name="groups">In the order of placement (nodes before boundary crossings).</param>
        /// <param name="obstacles">Rectangles that stay where they are (volumes in the squares).</param>
        /// <param name="boundary">Moved labels must stay inside it (null — anywhere).</param>
        /// <param name="height">Text height: gap between labels and step of the search.</param>
        public static void Place(IReadOnlyList<LabelGroup> groups, IReadOnlyList<LabelBox> obstacles, IReadOnlyList<XY> boundary, double height)
        {
            if (groups == null || groups.Count == 0) return;
            if (!(height > 0)) throw new ArgumentOutOfRangeException(nameof(height));
            double gap = 0.15 * height;
            double cell = Math.Max(height * 4, groups.Max(g => Math.Max(g.Box.MaxX - g.Box.MinX, g.Box.MaxY - g.Box.MinY)));
            var index = new BoxIndex(cell);
            foreach (var box in obstacles ?? new LabelBox[0]) index.Add(box);
            double step = 1.2 * height;
            int rings = 12;
            foreach (var group in groups)
            {
                if (!index.Hits(group.Box, gap))
                {
                    index.Add(group.Box);
                    continue;
                }
                bool placed = false;
                for (int ring = 1; ring <= rings && !placed; ring++)
                {
                    double r = ring * step;
                    for (int k = 0; k < Directions && !placed; k++)
                    {
                        double angle = 2 * Math.PI * k / Directions;
                        double dx = r * Math.Cos(angle), dy = r * Math.Sin(angle);
                        var candidate = group.Box.Moved(dx, dy);
                        if (boundary != null && !Inside(candidate, boundary)) continue;
                        if (index.Hits(candidate, gap)) continue;
                        group.Dx = dx;
                        group.Dy = dy;
                        group.Leader = true;
                        index.Add(candidate);
                        placed = true;
                    }
                }
                if (!placed)
                {
                    group.Overlapping = true;
                    index.Add(group.Box);
                }
            }
        }

        private static bool Inside(LabelBox box, IReadOnlyList<XY> boundary) =>
            Geometry.Contains(boundary, new XY(box.MinX, box.MinY)) && Geometry.Contains(boundary, new XY(box.MaxX, box.MinY)) &&
            Geometry.Contains(boundary, new XY(box.MaxX, box.MaxY)) && Geometry.Contains(boundary, new XY(box.MinX, box.MaxY));

        /// <summary>Uniform grid of rectangles for overlap queries.</summary>
        private sealed class BoxIndex
        {
            private readonly double cell;
            private readonly Dictionary<(long, long), List<LabelBox>> cells = new Dictionary<(long, long), List<LabelBox>>();

            internal BoxIndex(double cell) { this.cell = cell; }

            private long Key(double value) => (long)Math.Floor(value / cell);

            internal void Add(LabelBox box)
            {
                for (long x = Key(box.MinX); x <= Key(box.MaxX); x++)
                for (long y = Key(box.MinY); y <= Key(box.MaxY); y++)
                {
                    if (!cells.TryGetValue((x, y), out var list)) cells[(x, y)] = list = new List<LabelBox>();
                    list.Add(box);
                }
            }

            internal bool Hits(LabelBox box, double gap)
            {
                for (long x = Key(box.MinX - gap); x <= Key(box.MaxX + gap); x++)
                for (long y = Key(box.MinY - gap); y <= Key(box.MaxY + gap); y++)
                {
                    if (!cells.TryGetValue((x, y), out var list)) continue;
                    foreach (var other in list)
                        if (box.Overlaps(other, gap)) return true;
                }
                return false;
            }
        }
    }
}
