using System.Collections.Generic;

namespace Kzm.Core
{
    public sealed class DuplicatePair
    {
        public DuplicatePair(int first, int second, bool secondIsExisting, double distance)
        {
            First = first;
            Second = second;
            SecondIsExisting = secondIsExisting;
            Distance = distance;
        }

        /// <summary>Index in the list of new points.</summary>
        public int First { get; }
        /// <summary>Index in the new list, or in the existing list when <see cref="SecondIsExisting"/>.</summary>
        public int Second { get; }
        public bool SecondIsExisting { get; }
        public double Distance { get; }
    }

    /// <summary>Points closer than the tolerance (strictly less) go to the report; nothing is dropped.</summary>
    public static class DuplicateFinder
    {
        public static IReadOnlyList<DuplicatePair> Find(IReadOnlyList<XY> added, IReadOnlyList<XY> existing, double tolerance)
        {
            Guard.List(added, nameof(added));
            Guard.List(existing, nameof(existing));
            Guard.Positive(tolerance, "Допуск совпадения точек");
            var result = new List<DuplicatePair>();

            var existingGrid = new SpatialGrid(tolerance);
            foreach (var point in existing) existingGrid.Add(point);
            var addedGrid = new SpatialGrid(tolerance);

            for (int i = 0; i < added.Count; i++)
            {
                var point = added[i];
                foreach (int j in existingGrid.Within(point, tolerance))
                {
                    double distance = point.DistanceTo(existing[j]);
                    if (distance < tolerance) result.Add(new DuplicatePair(i, j, true, distance));
                }
                foreach (int j in addedGrid.Within(point, tolerance))
                {
                    double distance = point.DistanceTo(added[j]);
                    if (distance < tolerance) result.Add(new DuplicatePair(j, i, false, distance));
                }
                addedGrid.Add(point);
            }
            return result;
        }
    }
}
