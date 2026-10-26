using System;
using System.Collections.Generic;

namespace Profiles.Core
{
    /// <summary>
    /// Drawing-only ordinates: measured offsets, project vertices and exact
    /// measured/project intersections. These points never replace raw inputs.
    /// </summary>
    public static class SectionDisplayGeometry
    {
        public static List<SectionPoint> Build(SectionEarthworks result)
        {
            return BuildInternal(result, null);
        }

        /// <summary>
        /// Same drawing ordinates, with elevations only at each curve's own
        /// vertices. Authored manual anchors must be supplied before interpolation.
        /// </summary>
        public static List<SectionPoint> BuildTable(SectionEarthworks result, CrossSection entered)
        {
            if (entered == null) throw new ArgumentNullException(nameof(entered));
            return BuildInternal(result, entered);
        }

        private static List<SectionPoint> BuildInternal(SectionEarthworks result, CrossSection entered)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var section = result.ResolvedSection;
            if (section == null || section.Points == null)
                throw new ProfileValidationException("Не определена геометрия поперечника для дополнительных ординат.");
            var offsets = new SortedSet<double>();
            foreach (var point in section.Points) offsets.Add(point.Offset);
            var rawOffsets = new HashSet<double>(offsets);
            var manualAnchorOffsets = new HashSet<double>();
            if (entered != null)
            {
                if (entered.Points == null || entered.Points.Count != section.Points.Count)
                    throw new ArgumentException("Исходные и расчётные точки поперечника не совпадают.", nameof(entered));
                for (int index = 0; index < entered.Points.Count; index++)
                    if (entered.Points[index].Design.HasValue ||
                        (entered.UseLongitudinalProjectBottom && entered.ProjectAnchorIndex == index))
                        manualAnchorOffsets.Add(section.Points[index].Offset);
            }
            var vertexCoordinates = new Dictionary<double, double>();
            var existingRuns = SectionGeometry.Runs(section.Points, false);
            var mode = ProfileInterpolation.EffectiveDesignMode(section);
            var designRuns = mode == SectionDesignMode.Points
                ? SectionGeometry.Runs(section.Points, true) : new List<List<Point2>>();
            if (mode == SectionDesignMode.Trapezoid && result.DesignCurve.Count > 0)
                designRuns.Add(result.DesignCurve);
            foreach (var run in designRuns)
                for (int index = 0; index < run.Count; index++)
                {
                    var point = run[index];
                    bool daylight = mode == SectionDesignMode.Trapezoid && (index == 0 || index == run.Count - 1);
                    // A narrow project bottom can span only a few ULPs at a large
                    // survey origin. Never collapse its knees onto each other or
                    // onto a measured center merely because their X values are near.
                    double canonical = AddDerivedOffset(offsets, rawOffsets, point.X, candidate =>
                        daylight && rawOffsets.Contains(candidate) &&
                        SameElevation(Sample(section.Points, candidate, false), point.Y));
                    if (!vertexCoordinates.ContainsKey(canonical)) vertexCoordinates.Add(canonical, point.X);
                }
            AddIntersections(offsets, rawOffsets, Segments(existingRuns), Segments(designRuns));

            var display = new List<SectionPoint>(offsets.Count);
            foreach (double offset in offsets)
            {
                // A computed daylight may differ from its authored survey vertex
                // by a few ULPs. Sample the exact design vertex after snapping the
                // drawing ordinate so it does not become an out-of-domain blank.
                double designOffset;
                if (!vertexCoordinates.TryGetValue(offset, out designOffset)) designOffset = offset;
                double? design = mode == SectionDesignMode.Points ? Sample(section.Points, designOffset, true) :
                    mode == SectionDesignMode.Trapezoid ? SectionGeometry.InterpolateStrict(result.DesignCurve, designOffset) : null;
                double? existing = Sample(section.Points, offset, false);
                if (entered != null)
                {
                    // Ordinates used to draw/intersect two curves do not create
                    // surveyed elevations or new authored design anchors.
                    if (!rawOffsets.Contains(offset)) existing = null;
                    bool ownDesignVertex = mode == SectionDesignMode.Points
                        ? manualAnchorOffsets.Contains(offset) : vertexCoordinates.ContainsKey(offset);
                    if (!ownDesignVertex) design = null;
                }
                display.Add(new SectionPoint
                {
                    Offset = offset,
                    Existing = existing,
                    Design = design,
                    DistanceFromPrevious = display.Count == 0 ? 0 : offset - display[display.Count - 1].Offset
                });
            }
            return display;
        }

        private static double? Sample(IList<SectionPoint> points, double x, bool design)
        {
            if (points.Count == 0 || x < points[0].Offset || x > points[points.Count - 1].Offset) return null;
            int low = 0, high = points.Count - 1;
            while (high - low > 1)
            {
                int middle = low + (high - low) / 2;
                if (points[middle].Offset <= x) low = middle; else high = middle;
            }
            var a = points[low]; var b = points[high];
            double? first = design ? a.Design : a.Existing;
            double? last = design ? b.Design : b.Existing;
            if (a.Offset == x) return first;
            if (b.Offset == x) return last;
            if (!first.HasValue || !last.HasValue || b.Offset <= a.Offset) return null;
            return first.Value + (last.Value - first.Value) * ((x - a.Offset) / (b.Offset - a.Offset));
        }

        private struct Segment
        {
            internal Point2 First, Last;
            internal double Height(double x) => First.Y + (Last.Y - First.Y) * ((x - First.X) / (Last.X - First.X));
        }

        private static List<Segment> Segments(List<List<Point2>> runs)
        {
            var result = new List<Segment>();
            foreach (var run in runs)
                for (int index = 1; index < run.Count; index++)
                    if (run[index].X > run[index - 1].X)
                        result.Add(new Segment { First = run[index - 1], Last = run[index] });
            return result;
        }

        private static void AddIntersections(SortedSet<double> offsets, HashSet<double> rawOffsets, List<Segment> existing, List<Segment> design)
        {
            // Both segment lists are ordered and gaps have no segments. Sweep
            // their overlapping domains once; never interpolate across a gap.
            int first = 0, second = 0;
            while (first < existing.Count && second < design.Count)
            {
                var a = existing[first]; var b = design[second];
                double left = Math.Max(a.First.X, b.First.X), right = Math.Min(a.Last.X, b.Last.X);
                if (right >= left)
                {
                    double d0 = a.Height(left) - b.Height(left), d1 = a.Height(right) - b.Height(right);
                    Func<double, bool> coincident = candidate => SameElevation(a.Height(candidate), b.Height(candidate));
                    if (d0 == 0) AddDerivedOffset(offsets, rawOffsets, left, coincident);
                    if (d1 == 0) AddDerivedOffset(offsets, rawOffsets, right, coincident);
                    if ((d0 > 0 && d1 < 0) || (d0 < 0 && d1 > 0))
                        AddDerivedOffset(offsets, rawOffsets, left + (right - left) * (d0 / (d0 - d1)), coincident);
                }
                if (a.Last.X <= b.Last.X) first++;
                if (b.Last.X <= a.Last.X) second++;
            }
        }

        private static double AddDerivedOffset(SortedSet<double> offsets, HashSet<double> rawOffsets, double value,
            Func<double, bool> canSnap)
        {
            if (offsets.Contains(value)) return value;
            // Only derived values are normalized. Every authored offset is kept,
            // however close it lies to another authored point. The tolerance is
            // sixteen machine epsilons at this coordinate, capped at one micron.
            double tolerance = Math.Min(1e-6, 16 * 2.2204460492503131e-16 * Math.Max(1, Math.Abs(value)));
            double? nearestRaw = null, nearestOther = null;
            double rawDistance = double.PositiveInfinity, otherDistance = double.PositiveInfinity;
            foreach (double candidate in offsets.GetViewBetween(value - tolerance, value + tolerance))
            {
                if (!canSnap(candidate)) continue;
                double distance = Math.Abs(candidate - value);
                if (rawOffsets.Contains(candidate) && distance < rawDistance)
                { nearestRaw = candidate; rawDistance = distance; }
                else if (distance < otherDistance)
                { nearestOther = candidate; otherDistance = distance; }
            }
            if (nearestRaw.HasValue) return nearestRaw.Value;
            if (nearestOther.HasValue) return nearestOther.Value;
            offsets.Add(value);
            return value;
        }

        private static bool SameElevation(double? first, double second)
        {
            if (!first.HasValue) return false;
            double tolerance = Math.Min(1e-6, 16 * 2.2204460492503131e-16 *
                Math.Max(1, Math.Max(Math.Abs(first.Value), Math.Abs(second))));
            return Math.Abs(first.Value - second) <= tolerance;
        }
    }
}
