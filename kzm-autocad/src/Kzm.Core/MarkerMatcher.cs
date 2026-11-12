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
    /// Способ (б): XY берётся у ближайшего маркера в радиусе R от точки вставки текста.
    /// Неоднозначности не решаются молча: нет маркера в радиусе R; второй маркер
    /// дальше первого не больше чем на допуск совпадения; один маркер ближайший
    /// сразу к нескольким отметкам — все такие тексты становятся спорными.
    /// </summary>
    public static class MarkerMatcher
    {
        public static IReadOnlyList<MarkerMatch> Match(IReadOnlyList<XY> anchors, IReadOnlyList<XY> markers,
            double radius, double tieTolerance, int decimals)
        {
            Guard.List(anchors, nameof(anchors));
            Guard.List(markers, nameof(markers));
            Guard.Positive(radius, "Радиус поиска маркера R");
            if (!Guard.Finite(tieTolerance) || tieTolerance < 0) throw new KzmException("Допуск совпадения задан неверно.");

            var grid = new SpatialGrid(radius);
            foreach (var marker in markers) grid.Add(marker);

            var result = new MarkerMatch[anchors.Count];
            for (int i = 0; i < anchors.Count; i++)
            {
                var near = grid.Within(anchors[i], radius)
                    .Select(index => new { index, distance = markers[index].DistanceTo(anchors[i]) })
                    .OrderBy(item => item.distance).ThenBy(item => item.index)
                    .Take(2).ToList();
                if (near.Count == 0)
                {
                    result[i] = new MarkerMatch(-1, double.NaN, ImportReason.NoMarker,
                        "нет маркера в радиусе " + ElevationFormat.Format(radius, decimals) + " м");
                    continue;
                }
                if (near.Count == 2 && near[1].distance - near[0].distance <= tieTolerance)
                {
                    result[i] = new MarkerMatch(-1, near[0].distance, ImportReason.MarkerTie,
                        "два маркера на одинаковом расстоянии (" + ElevationFormat.Format(near[0].distance, 3) + " и " +
                        ElevationFormat.Format(near[1].distance, 3) + " м)");
                    continue;
                }
                result[i] = new MarkerMatch(near[0].index, near[0].distance, ImportReason.None, string.Empty);
            }

            var shared = result.Select((match, index) => new { match, index })
                .Where(item => item.match.Success)
                .GroupBy(item => item.match.MarkerIndex)
                .Where(group => group.Count() > 1);
            foreach (var group in shared)
            {
                int count = group.Count();
                foreach (var item in group)
                    result[item.index] = new MarkerMatch(-1, item.match.Distance, ImportReason.MarkerShared,
                        "маркер ближайший сразу к " + count + " отметкам");
            }
            return result;
        }
    }
}
