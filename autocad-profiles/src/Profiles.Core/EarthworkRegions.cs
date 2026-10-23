using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>
    /// Union of ordered, nonoverlapping vertical integration strips. The union is
    /// represented by lower/upper chains rather than a general polygon boolean,
    /// so no numerical snapping, bridging, holes or area changes are introduced.
    /// </summary>
    internal static class EarthworkRegions
    {
        internal static void MergeAdjacent(List<List<Point2>> regions)
        {
            if (regions.Count < 2) return;
            var merged = new List<List<Point2>>();
            List<Point2> lower = null, upper = null;
            double right = 0, lastBottom = 0, lastTop = 0;
            foreach (var strip in regions)
            {
                double left = strip.Min(point => point.X), end = strip.Max(point => point.X);
                double bottom0 = strip.Where(point => point.X == left).Min(point => point.Y);
                double top0 = strip.Where(point => point.X == left).Max(point => point.Y);
                double bottom1 = strip.Where(point => point.X == end).Min(point => point.Y);
                double top1 = strip.Where(point => point.X == end).Max(point => point.Y);
                bool sharesInteriorEdge = lower != null && left == right &&
                    Math.Min(lastTop, top0) > Math.Max(lastBottom, bottom0);
                if (!sharesInteriorEdge)
                {
                    if (lower != null) merged.Add(Boundary(lower, upper));
                    lower = new List<Point2>();
                    upper = new List<Point2>();
                }
                // Different heights at the same X are real vertical silt steps.
                // Positive overlap guarantees these two boundary chains cannot
                // cross or pinch at the shared edge.
                Append(lower, new Point2(left, bottom0));
                Append(lower, new Point2(end, bottom1));
                Append(upper, new Point2(left, top0));
                Append(upper, new Point2(end, top1));
                right = end;
                lastBottom = bottom1;
                lastTop = top1;
            }
            if (lower != null) merged.Add(Boundary(lower, upper));
            regions.Clear();
            regions.AddRange(merged);
        }

        private static List<Point2> Boundary(List<Point2> lower, List<Point2> upper)
        {
            var result = new List<Point2>(lower.Count + upper.Count);
            foreach (var point in lower) Append(result, point);
            for (int index = upper.Count - 1; index >= 0; index--) Append(result, upper[index]);
            if (result.Count > 1 && Same(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            return result;
        }

        private static void Append(List<Point2> chain, Point2 point)
        {
            if (chain.Count == 0 || !Same(chain[chain.Count - 1], point)) chain.Add(point);
        }

        private static bool Same(Point2 a, Point2 b) => a.X == b.X && a.Y == b.Y;
    }
}
