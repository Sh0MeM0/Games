using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    public sealed class SectionEarthworks
    {
        public bool IsValid { get; internal set; }
        public string Status { get; internal set; } = "Расчёт не выполнен.";
        public double? CutArea { get; internal set; }
        public double? FillArea { get; internal set; }
        public double? SiltArea { get; internal set; }
        public CrossSection ResolvedSection { get; internal set; }
        public List<Point2> DesignCurve { get; } = new List<Point2>();
        public List<List<Point2>> CleanedRuns { get; } = new List<List<Point2>>();
        public List<List<Point2>> CutRegions { get; } = new List<List<Point2>>();
        public List<List<Point2>> FillRegions { get; } = new List<List<Point2>>();
        public List<List<Point2>> SiltRegions { get; } = new List<List<Point2>>();
        public List<string> Warnings { get; } = new List<string>();
    }

    public sealed class StationEarthworks
    {
        public double Station { get; internal set; }
        public SectionEarthworks Result { get; internal set; }
    }

    public sealed class EarthworkVolumeInterval
    {
        public double StartStation { get; internal set; }
        public double EndStation { get; internal set; }
        public double? CutVolume { get; internal set; }
        public double? FillVolume { get; internal set; }
        public double? SiltVolume { get; internal set; }
        public string Status { get; internal set; }
    }

    public sealed class EarthworksSummary
    {
        public List<StationEarthworks> Stations { get; } = new List<StationEarthworks>();
        public List<EarthworkVolumeInterval> Intervals { get; } = new List<EarthworkVolumeInterval>();
        public List<string> Warnings { get; } = new List<string>();
        public bool HasUnassignedSections { get; internal set; }
        public double? TotalCutVolume { get; internal set; }
        public double? TotalFillVolume { get; internal set; }
        public double? TotalSiltVolume { get; internal set; }
        public double KnownCutVolume => Intervals.Sum(interval => interval.CutVolume ?? 0);
        public double KnownFillVolume => Intervals.Sum(interval => interval.FillVolume ?? 0);
        public double KnownSiltVolume => Intervals.Sum(interval => interval.SiltVolume ?? 0);
    }

    /// <summary>Exact piecewise-linear areas in m², and average-end-area volumes in m³.</summary>
    public static class Earthworks
    {
        public static SectionEarthworks Calculate(CrossSection section, IList<LongitudinalPoint> longitudinal = null)
        {
            var result = new SectionEarthworks();
            try
            {
                var resolved = ProfileInterpolation.ResolveSection(section, longitudinal);
                result.ResolvedSection = resolved;
                var wrapper = new ProfileProject { Kind = ProfileKind.CrossSection, Sections = new List<CrossSection> { resolved } };
                ProfileValidation.Check(wrapper, geometry: false);
                var existing = SectionGeometry.Runs(resolved.Points, false);
                if (resolved.Points.Count < 2)
                    throw new ProfileValidationException("Для расчёта площадей нужны не менее двух точек поперечника.");
                var mode = ProfileInterpolation.EffectiveDesignMode(resolved);
                try
                {
                    if (mode == SectionDesignMode.Trapezoid)
                    {
                        if (!resolved.ProjectBottom.HasValue || !resolved.BottomWidth.HasValue || !resolved.LeftSlope.HasValue || !resolved.RightSlope.HasValue)
                            throw new ProfileValidationException("Для проектной трапеции нужны отметка дна, ширина и оба заложения откосов.");
                        // Design remains fixed against the original measured terrain.
                        // Removing silt never silently moves the project daylight points.
                        result.DesignCurve.AddRange(SectionGeometry.Trapezoid(resolved));
                    }
                    else if (mode == SectionDesignMode.Points)
                        result.DesignCurve.AddRange(resolved.Points.Where(point => point.Design.HasValue)
                            .Select(point => new Point2(point.Offset, point.Design.Value)));
                }
                catch (ProfileValidationException error) { result.Warnings.Add(error.Message); }

                for (int index = result.DesignCurve.Count - 1; index > 0; index--)
                    if (result.DesignCurve[index].X == result.DesignCurve[index - 1].X && result.DesignCurve[index].Y == result.DesignCurve[index - 1].Y)
                        result.DesignCurve.RemoveAt(index);
                double thickness = resolved.SiltThickness ?? 0;
                if (resolved.SiltThickness.HasValue && thickness <= 0)
                    throw new ProfileValidationException("Толщина ила должна быть больше нуля; пустое поле означает отсутствие ила.");
                double? siltLeft = null, siltRight = null;
                bool siltValid = true;
                if (thickness > 0)
                {
                    double? defaultLeft = result.DesignCurve.Count >= 2 ? result.DesignCurve[0].X :
                        mode == SectionDesignMode.None && existing.Any(run => run.Count > 0) ? existing.First(run => run.Count > 0)[0].X : (double?)null;
                    double? defaultRight = result.DesignCurve.Count >= 2 ? result.DesignCurve[result.DesignCurve.Count - 1].X :
                        mode == SectionDesignMode.None && existing.Any(run => run.Count > 0) ? existing.Last(run => run.Count > 0).Last().X : (double?)null;
                    siltLeft = resolved.SiltLeft ?? defaultLeft;
                    siltRight = resolved.SiltRight ?? defaultRight;
                    if (!siltLeft.HasValue || !siltRight.HasValue || siltLeft.Value >= siltRight.Value)
                    {
                        result.Warnings.Add("Для удаления ила задайте левую и правую границы в пределах измеренного поперечника.");
                        siltValid = false;
                    }
                    else
                    {
                        var coverage = CoveringRun(existing, siltLeft.Value, siltRight.Value);
                        if (coverage == null)
                        {
                            result.Warnings.Add("Площадь ила не рассчитана: весь заданный диапазон должен быть покрыт натурными отметками без пропусков.");
                            siltValid = false;
                        }
                        else
                        {
                            var knots = Knots(siltLeft.Value, siltRight.Value, coverage.Select(point => point.X));
                            for (int index = 1; index < knots.Count; index++)
                            {
                                double x0 = knots[index - 1], x1 = knots[index];
                                double top0 = Height(coverage, x0), top1 = Height(coverage, x1);
                                AddRegion(result.SiltRegions, x0, top0, top0 - thickness, x1, top1, top1 - thickness);
                            }
                            result.SiltArea = thickness * (siltRight.Value - siltLeft.Value);
                        }
                    }
                }
                else result.SiltArea = 0;

                // Keep discontinuities at silt limits vertical rather than spreading
                // them into artificial ramps across neighboring measured segments.
                result.CleanedRuns.AddRange(Cleaned(existing, thickness, siltLeft, siltRight));
                if (result.DesignCurve.Count < 2)
                    result.Warnings.Add("Выемка и насыпь не рассчитаны: проектная линия отсутствует или содержит меньше двух точек.");
                else if (!siltValid)
                    result.Warnings.Add("Выемка и насыпь не рассчитаны до исправления диапазона удаления ила.");
                else
                {
                    double left = result.DesignCurve[0].X, right = result.DesignCurve.Last().X;
                    var coverage = CoveringRun(existing, left, right);
                    if (coverage == null)
                        result.Warnings.Add("Выемка и насыпь не рассчитаны: натурная съёмка должна покрывать весь проектный диапазон без пропусков.");
                    else
                    {
                        var additions = coverage.Select(point => point.X).Concat(result.DesignCurve.Select(point => point.X));
                        if (siltLeft.HasValue) additions = additions.Concat(new[] { siltLeft.Value, siltRight.Value });
                        var knots = Knots(left, right, additions);
                        double cut = 0, fill = 0;
                        for (int index = 1; index < knots.Count; index++)
                        {
                            double x0 = knots[index - 1], x1 = knots[index];
                            double middle = x0 + (x1 - x0) / 2;
                            double removed = thickness > 0 && siltLeft.HasValue && siltRight.HasValue && middle >= siltLeft.Value && middle <= siltRight.Value ? thickness : 0;
                            double existing0 = Height(coverage, x0) - removed, existing1 = Height(coverage, x1) - removed;
                            double design0 = Height(result.DesignCurve, x0), design1 = Height(result.DesignCurve, x1);
                            Integrate(result, x0, existing0, design0, x1, existing1, design1, ref cut, ref fill);
                        }
                        result.CutArea = cut;
                        result.FillArea = fill;
                    }
                }
                result.IsValid = result.CutArea.HasValue && result.FillArea.HasValue && result.SiltArea.HasValue;
                result.Status = result.Warnings.Count == 0 ? "Площади рассчитаны по полной натурной и проектной геометрии." : string.Join(" ", result.Warnings);
            }
            catch (ProfileValidationException error)
            {
                result.Warnings.Add(error.Message);
                result.Status = string.Join(" ", result.Warnings);
            }
            // Integration strips are numerical cells, not physical hatch regions.
            // Join only cells sharing a positive-length interior edge; zero-area
            // point contacts and unmeasured gaps remain separate boundaries.
            EarthworkRegions.MergeAdjacent(result.CutRegions);
            EarthworkRegions.MergeAdjacent(result.FillRegions);
            EarthworkRegions.MergeAdjacent(result.SiltRegions);
            return result;
        }

        public static EarthworksSummary CalculateVolumes(ProfileProject project)
        {
            var summary = new EarthworksSummary();
            if (project == null || project.Sections == null)
            { summary.Warnings.Add("Проект или список поперечников отсутствует."); return summary; }
            bool unsited = false;
            foreach (var section in project.Sections.Where(section => section == null || !section.Station.HasValue))
            {
                if (section != null && IsEmptyPlaceholder(section)) continue;
                unsited = true;
                summary.Warnings.Add("Есть заполненный поперечник без пикета; суммарные объёмы неполны. Назначьте ему пикет.");
            }
            summary.HasUnassignedSections = unsited;
            var located = project.Sections.Where(section => section != null && section.Station.HasValue).ToList();
            // An untouched editor template is not an unlocated measured section.
            if (located.Count == 0 && !unsited) return summary;
            if (located.Any(section => !NumberParser.Finite(section.Station.Value)))
            { summary.Warnings.Add("Некорректный пикет поперечника; объёмы не рассчитаны."); return summary; }
            foreach (var group in located.GroupBy(section => section.Station.Value).OrderBy(group => group.Key))
            {
                SectionEarthworks calculation;
                if (group.Count() != 1)
                {
                    calculation = new SectionEarthworks { Status = "Несколько поперечников имеют один пикет; выберите единственное сечение для расчёта объёмов." };
                    calculation.Warnings.Add(calculation.Status);
                }
                else calculation = Calculate(group.Single(), project.Longitudinal);
                summary.Stations.Add(new StationEarthworks { Station = group.Key, Result = calculation });
                if (!calculation.IsValid)
                    summary.Warnings.Add("ПК " + ProfileEngine.Station(group.Key) + ": " + calculation.Status);
            }
            for (int index = 1; index < summary.Stations.Count; index++)
            {
                var first = summary.Stations[index - 1];
                var last = summary.Stations[index];
                double length = last.Station - first.Station;
                var interval = new EarthworkVolumeInterval
                {
                    StartStation = first.Station, EndStation = last.Station,
                    CutVolume = Average(first.Result.CutArea, last.Result.CutArea, length),
                    FillVolume = Average(first.Result.FillArea, last.Result.FillArea, length),
                    SiltVolume = Average(first.Result.SiltArea, last.Result.SiltArea, length)
                };
                bool complete = interval.CutVolume.HasValue && interval.FillVolume.HasValue && interval.SiltVolume.HasValue;
                interval.Status = complete ? "Средняя площадь двух соседних поперечников × расстояние между пикетами." :
                    "Часть объёмов не рассчитана: отсутствуют достоверные площади одного из соседних поперечников.";
                if (unsited) interval.Status += " Частичная оценка: есть поперечник без пикета.";
                summary.Intervals.Add(interval);
            }
            if (summary.Intervals.Count == 0)
                summary.Warnings.Add("Для объёмов нужны как минимум два поперечника с разными пикетами.");
            // Sum the measured intervals independently. An unknown interval is
            // not zero and remains a barrier between its neighboring sections,
            // but does not erase volumes already calculated elsewhere. Keep a
            // genuinely unavailable quantity null instead of reporting a zero.
            if (summary.Intervals.Any(interval => interval.CutVolume.HasValue)) summary.TotalCutVolume = summary.KnownCutVolume;
            if (summary.Intervals.Any(interval => interval.FillVolume.HasValue)) summary.TotalFillVolume = summary.KnownFillVolume;
            if (summary.Intervals.Any(interval => interval.SiltVolume.HasValue)) summary.TotalSiltVolume = summary.KnownSiltVolume;
            return summary;
        }

        private static bool IsEmptyPlaceholder(CrossSection section) =>
            section.Points != null && section.Points.Select((point, index) =>
                point != null && Enum.IsDefined(typeof(SectionPointRole), point.Role) &&
                !point.Existing.HasValue && !point.Design.HasValue && point.Offset == 0 &&
                !point.UseLongitudinalElevation &&
                (!point.DistanceFromPrevious.HasValue || (index == 0 && point.DistanceFromPrevious.Value == 0))).All(empty => empty) &&
            (section.DesignMode == SectionDesignMode.None || section.DesignMode == SectionDesignMode.Automatic) &&
            !section.ProjectAnchorIndex.HasValue && section.CenterOffset == 0 &&
            !section.UseLongitudinalProjectBottom && !section.UseLongitudinalWater &&
            !section.LeftBank.HasValue && !section.RightBank.HasValue &&
            !section.ExistingBottom.HasValue && !section.Water.HasValue && !section.ProjectBottom.HasValue &&
            !section.BottomWidth.HasValue && !section.LeftSlope.HasValue && !section.RightSlope.HasValue &&
            !section.SiltThickness.HasValue && !section.SiltLeft.HasValue && !section.SiltRight.HasValue;

        private static double? Average(double? first, double? second, double length) =>
            first.HasValue && second.HasValue && length > 0 ? (first.Value + second.Value) / 2 * length : (double?)null;

        private static List<Point2> CoveringRun(List<List<Point2>> runs, double left, double right) =>
            right > left ? runs.FirstOrDefault(run => run.Count >= 2 && run[0].X <= left && run.Last().X >= right) : null;

        private static List<double> Knots(double left, double right, IEnumerable<double> additional) =>
            additional.Where(x => x > left && x < right).Concat(new[] { left, right }).Distinct().OrderBy(x => x).ToList();

        private static double Height(List<Point2> curve, double x)
        {
            double? elevation = SectionGeometry.InterpolateStrict(curve, x);
            if (!elevation.HasValue)
                throw new ProfileValidationException("Геометрия не покрывает требуемое расстояние; экстраполяция не выполняется.");
            return elevation.Value;
        }

        private static List<List<Point2>> Cleaned(List<List<Point2>> runs, double thickness, double? left, double? right)
        {
            var result = new List<List<Point2>>();
            foreach (var run in runs)
            {
                if (run.Count < 2) { result.Add(new List<Point2>(run)); continue; }
                var extra = left.HasValue && right.HasValue ? new[] { left.Value, right.Value } : new double[0];
                var knots = Knots(run[0].X, run.Last().X, run.Select(point => point.X).Concat(extra));
                var cleaned = new List<Point2>();
                for (int index = 1; index < knots.Count; index++)
                {
                    double x0 = knots[index - 1], x1 = knots[index];
                    double middle = x0 + (x1 - x0) / 2;
                    double removed = thickness > 0 && left.HasValue && right.HasValue && middle >= left.Value && middle <= right.Value ? thickness : 0;
                    var a = new Point2(x0, Height(run, x0) - removed);
                    var b = new Point2(x1, Height(run, x1) - removed);
                    if (cleaned.Count == 0 || cleaned.Last().X != a.X || cleaned.Last().Y != a.Y) cleaned.Add(a);
                    cleaned.Add(b);
                }
                result.Add(cleaned);
            }
            return result;
        }

        private static void Integrate(SectionEarthworks result, double x0, double existing0, double design0,
            double x1, double existing1, double design1, ref double cut, ref double fill)
        {
            double difference0 = existing0 - design0, difference1 = existing1 - design1;
            if ((difference0 > 0 && difference1 < 0) || (difference0 < 0 && difference1 > 0))
            {
                double fraction = difference0 / (difference0 - difference1);
                double crossing = x0 + (x1 - x0) * fraction;
                double elevation = design0 + (design1 - design0) * fraction;
                Integrate(result, x0, existing0, design0, crossing, elevation, elevation, ref cut, ref fill);
                Integrate(result, crossing, elevation, elevation, x1, existing1, design1, ref cut, ref fill);
                return;
            }
            double signed = (difference0 + difference1) / 2 * (x1 - x0);
            if (signed > 0)
            {
                cut += signed;
                AddRegion(result.CutRegions, x0, existing0, design0, x1, existing1, design1);
            }
            else if (signed < 0)
            {
                fill -= signed;
                AddRegion(result.FillRegions, x0, design0, existing0, x1, design1, existing1);
            }
        }

        private static void AddRegion(List<List<Point2>> regions, double x0, double top0, double bottom0,
            double x1, double top1, double bottom1)
        {
            if (x1 <= x0 || (top0 == bottom0 && top1 == bottom1)) return;
            var candidates = new[] { new Point2(x0, bottom0), new Point2(x1, bottom1), new Point2(x1, top1), new Point2(x0, top0) };
            var boundary = new List<Point2>();
            foreach (var point in candidates)
                if (!boundary.Any(existing => existing.X == point.X && existing.Y == point.Y)) boundary.Add(point);
            if (boundary.Count >= 3) regions.Add(boundary);
        }
    }
}
