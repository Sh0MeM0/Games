using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>Distance-weighted interpolation of missing elevations; input anchors are never changed.</summary>
    public static class ProfileInterpolation
    {
        private static readonly Func<LongitudinalPoint, double?>[] Elevations =
        {
            p => p.Ground, p => p.LeftBank, p => p.RightBank,
            p => p.ExistingBottom, p => p.Water, p => p.ProjectBottom,
            p => p.ProjectGround, p => p.PipeInvert
        };

        public static ProfileProject ResolveLongitudinal(ProfileProject source)
        {
            var result = ProjectFile.Clone(source);
            result.Longitudinal = ResolveLongitudinal(source.Longitudinal);
            return result;
        }

        public static List<LongitudinalPoint> ResolveLongitudinal(IList<LongitudinalPoint> source)
        {
            CheckLongitudinal(source);
            var result = source.Select(Copy).ToList();
            // Each column has independent anchors; using raw input prevents derived
            // values from becoming fixed anchors when another value is edited.
            for (int column = 0; column < Elevations.Length; column++)
            {
                var get = Elevations[column];
                int previous = -1;
                for (int next = 0; next < source.Count; next++)
                {
                    if (!get(source[next]).HasValue) continue;
                    if (previous >= 0)
                        for (int index = previous + 1; index < next; index++)
                            Set(result[index], column, Between(source[previous].Station, get(source[previous]).Value,
                                source[next].Station, get(source[next]).Value, source[index].Station));
                    previous = next;
                }
            }
            return result;
        }

        public static LongitudinalPoint SampleLongitudinal(IList<LongitudinalPoint> source, double station)
        {
            CheckLongitudinal(source);
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж");
            var exact = source.FirstOrDefault(p => p.Station == station);
            var sample = exact == null ? new LongitudinalPoint { Station = station } : Copy(exact);
            for (int column = 0; column < Elevations.Length; column++)
                Set(sample, column, ValueAt(source, p => p.Station, Elevations[column], station));
            return sample;
        }

        /// <summary>
        /// Keeps longitudinal elevations as pending seeds. Surveyed bank/bottom
        /// distances must be supplied by the user before they become cross points.
        /// </summary>
        public static CrossSection CreateSection(IList<LongitudinalPoint> source, double station)
        {
            var sample = SampleLongitudinal(source, station);
            return new CrossSection
            {
                Name = "ПК " + ProfileEngine.Station(station), Station = station,
                LeftBank = sample.LeftBank, RightBank = sample.RightBank,
                ExistingBottom = sample.ExistingBottom, Water = sample.Water,
                ProjectBottom = sample.ProjectBottom, BottomWidth = sample.BottomWidth,
                LeftSlope = sample.LeftSlope, RightSlope = sample.RightSlope,
                UsePointDistances = true, UseLongitudinalProjectBottom = true, UseLongitudinalWater = true,
                ProjectAnchorIndex = 1,
                Points = new List<SectionPoint>
                {
                    new SectionPoint { Role = SectionPointRole.LeftBank, Existing = sample.LeftBank, UseLongitudinalElevation = true, DistanceFromPrevious = 0 },
                    new SectionPoint { Role = SectionPointRole.Bottom, Existing = sample.ExistingBottom, UseLongitudinalElevation = true },
                    new SectionPoint { Role = SectionPointRole.RightBank, Existing = sample.RightBank, UseLongitudinalElevation = true }
                }
            };
        }

        public static SectionDesignMode EffectiveDesignMode(CrossSection section)
        {
            if (section == null) throw new ProfileValidationException("Поперечник не задан.");
            if (section.DesignMode != SectionDesignMode.Automatic) return section.DesignMode;
            if (new[] { section.ProjectBottom, section.BottomWidth, section.LeftSlope, section.RightSlope }.Any(value => value.HasValue))
                return SectionDesignMode.Trapezoid;
            return section.Points != null && section.Points.Any(point => point != null && point.Design.HasValue)
                ? SectionDesignMode.Points : SectionDesignMode.None;
        }

        /// <summary>Resolves a section copy; incomplete surveyed intervals remain null in the saved raw section.</summary>
        public static CrossSection ResolveSection(CrossSection source, IList<LongitudinalPoint> longitudinal = null)
        {
            var result = CloneSection(source);
            LongitudinalPoint station = null;
            bool linked = source.UseLongitudinalProjectBottom || source.UseLongitudinalWater || source.Points.Any(point => point.UseLongitudinalElevation);
            if (linked && longitudinal == null)
                throw new ProfileValidationException("Для связанных отметок передайте текущие данные продольного профиля; сохранённые опорные значения не заменяют актуальный расчёт.");
            if (longitudinal != null && linked)
            {
                if (!source.Station.HasValue) throw new ProfileValidationException("Для связанных отметок укажите пикет поперечника.");
                station = SampleLongitudinal(longitudinal, source.Station.Value);
            }
            if (station != null)
            {
                if (source.UseLongitudinalProjectBottom) result.ProjectBottom = station.ProjectBottom;
                if (source.UseLongitudinalWater) result.Water = station.Water;
            }
            double offset = result.Points.Count == 0 ? 0 : result.Points[0].Offset;
            for (int index = 0; index < result.Points.Count; index++)
            {
                var point = result.Points[index];
                if (source.UsePointDistances)
                {
                    if (index == 0 && point.DistanceFromPrevious.HasValue && point.DistanceFromPrevious.Value != 0)
                        throw new ProfileValidationException("У первой точки расстояние от предыдущей должно быть пустым или нулевым.");
                    if (index > 0)
                    {
                        if (!point.DistanceFromPrevious.HasValue || !NumberParser.Finite(point.DistanceFromPrevious.Value) || point.DistanceFromPrevious.Value <= 0)
                            throw new ProfileValidationException("Поперечник «" + source.Name + "»: введите положительное расстояние перед точкой " + (index + 1) + ".");
                        offset += point.DistanceFromPrevious.Value;
                    }
                    ProfileValidation.Range(offset, -1000000000, 1000000000, "Суммарное смещение поперечника");
                    point.Offset = offset;
                }
                if (station != null && point.UseLongitudinalElevation)
                {
                    if (point.Role == SectionPointRole.LeftBank) point.Existing = station.LeftBank;
                    else if (point.Role == SectionPointRole.Bottom) point.Existing = station.ExistingBottom;
                    else if (point.Role == SectionPointRole.RightBank) point.Existing = station.RightBank;
                }
            }
            CheckSection(result.Points);
            result.UsePointDistances = false;
            if (source.ProjectAnchorIndex.HasValue)
            {
                if (source.ProjectAnchorIndex.Value < 0 || source.ProjectAnchorIndex.Value >= result.Points.Count)
                    throw new ProfileValidationException("Выбранная проектная опорная точка отсутствует в поперечнике.");
                result.CenterOffset = result.Points[source.ProjectAnchorIndex.Value].Offset;
            }
            var mode = EffectiveDesignMode(result);
            if (source.DesignMode == SectionDesignMode.Automatic && mode == SectionDesignMode.Trapezoid && source.Points.Any(point => point.Design.HasValue))
                throw new ProfileValidationException("Выберите способ проектирования: трапеция или проектные отметки по точкам.");
            result.DesignMode = mode;
            if (mode != SectionDesignMode.Points)
                foreach (var point in result.Points) point.Design = null;
            else
            {
                // A selected manual design anchor is the same shared project
                // elevation as the longitudinal cell. Resolve the live value
                // before filling intermediate design cells, without fixing it
                // into the saved raw survey buffer.
                if (station != null && source.UseLongitudinalProjectBottom && source.ProjectAnchorIndex.HasValue)
                    result.Points[source.ProjectAnchorIndex.Value].Design = station.ProjectBottom;
                // Use only raw anchors so intermediate cells recompute after each edit.
                var anchors = result.Points.Select(point => new SectionPoint { Offset = point.Offset, Design = point.Design }).ToList();
                int previous = -1;
                for (int next = 0; next < anchors.Count; next++)
                {
                    if (!anchors[next].Design.HasValue) continue;
                    if (previous >= 0)
                        for (int index = previous + 1; index < next; index++)
                            result.Points[index].Design = Between(anchors[previous].Offset, anchors[previous].Design.Value,
                                anchors[next].Offset, anchors[next].Design.Value, anchors[index].Offset);
                    previous = next;
                }
            }
            if (mode != SectionDesignMode.Trapezoid)
                result.ProjectBottom = result.BottomWidth = result.LeftSlope = result.RightSlope = null;
            return result;
        }

        public static CrossSection CloneSection(CrossSection source)
        {
            if (source == null || source.Points == null || source.Points.Any(point => point == null))
                throw new ProfileValidationException("Поперечник или его точки отсутствуют.");
            return new CrossSection
            {
                Name = source.Name, Station = source.Station, DesignMode = source.DesignMode,
                UsePointDistances = source.UsePointDistances, ProjectAnchorIndex = source.ProjectAnchorIndex,
                UseLongitudinalProjectBottom = source.UseLongitudinalProjectBottom, UseLongitudinalWater = source.UseLongitudinalWater,
                LeftBank = source.LeftBank, RightBank = source.RightBank, ExistingBottom = source.ExistingBottom,
                Water = source.Water, ProjectBottom = source.ProjectBottom, BottomWidth = source.BottomWidth,
                LeftSlope = source.LeftSlope, RightSlope = source.RightSlope, CenterOffset = source.CenterOffset,
                SiltThickness = source.SiltThickness, SiltLeft = source.SiltLeft, SiltRight = source.SiltRight,
                Points = source.Points.Select(point => new SectionPoint
                {
                    Offset = point.Offset, Existing = point.Existing, Design = point.Design,
                    Role = point.Role, DistanceFromPrevious = point.DistanceFromPrevious,
                    UseLongitudinalElevation = point.UseLongitudinalElevation
                }).ToList()
            };
        }

        public static double? SectionElevationAt(IList<SectionPoint> source, double offset, bool design = false)
        {
            CheckSection(source);
            ProfileValidation.Range(offset, -1000000000, 1000000000, "Смещение поперечника");
            return ValueAt(source, p => p.Offset, p => design ? p.Design : p.Existing, offset);
        }

        /// <summary>
        /// Chooses the lowest measured point or the middle of a contiguous flat
        /// bottom. If equal lows belong to different depressions, uses the run
        /// nearest the middle of the surveyed span, then the leftmost tie.
        /// </summary>
        public static double FindCenterOffset(IList<SectionPoint> source)
        {
            CheckSection(source);
            var known = source.Where(p => p.Existing.HasValue).ToList();
            if (known.Count == 0)
                throw new ProfileValidationException("Для определения центра проектного дна введите фактические отметки и расстояния поперечника.");
            double minimum = known.Min(p => p.Existing.Value);
            double middle = known[0].Offset + (known[known.Count - 1].Offset - known[0].Offset) / 2;
            var centers = new List<double>();
            for (int index = 0; index < source.Count; index++)
            {
                if (source[index].Existing != minimum) continue;
                int end = index;
                while (end + 1 < source.Count && source[end + 1].Existing == minimum) end++;
                centers.Add(source[index].Offset + (source[end].Offset - source[index].Offset) / 2);
                index = end;
            }
            return centers.OrderBy(x => Math.Abs(x - middle)).ThenBy(x => x).First();
        }

        private static double? ValueAt<T>(IList<T> points, Func<T, double> coordinate, Func<T, double?> value, double target)
        {
            int left = -1;
            for (int index = 0; index < points.Count; index++)
            {
                if (!value(points[index]).HasValue) continue;
                double x = coordinate(points[index]);
                if (x == target) return value(points[index]);
                if (x < target) { left = index; continue; }
                if (left < 0) return null;
                return Between(coordinate(points[left]), value(points[left]).Value, x, value(points[index]).Value, target);
            }
            return null;
        }

        private static double Between(double x0, double y0, double x1, double y1, double x) =>
            y0 + (y1 - y0) * ((x - x0) / (x1 - x0));

        private static LongitudinalPoint Copy(LongitudinalPoint point) => new LongitudinalPoint
        {
            Station = point.Station, Ground = point.Ground, LeftBank = point.LeftBank,
            RightBank = point.RightBank, ExistingBottom = point.ExistingBottom,
            Water = point.Water, ProjectBottom = point.ProjectBottom, BottomWidth = point.BottomWidth,
            LeftSlope = point.LeftSlope, RightSlope = point.RightSlope,
            ProjectGround = point.ProjectGround, PipeInvert = point.PipeInvert,
            PipeDescription = point.PipeDescription, Foundation = point.Foundation,
            PointName = point.PointName, PeatDepth = point.PeatDepth
        };

        private static void Set(LongitudinalPoint point, int column, double? value)
        {
            switch (column)
            {
                case 0: point.Ground = value; break;
                case 1: point.LeftBank = value; break;
                case 2: point.RightBank = value; break;
                case 3: point.ExistingBottom = value; break;
                case 4: point.Water = value; break;
                case 5: point.ProjectBottom = value; break;
                case 6: point.ProjectGround = value; break;
                case 7: point.PipeInvert = value; break;
            }
        }

        private static void CheckLongitudinal(IList<LongitudinalPoint> source)
        {
            if (source == null) throw new ProfileValidationException("Список пикетов отсутствует.");
            if (source.Count > 10000) throw new ProfileValidationException("Допустимо не более 10000 пикетов.");
            double previous = double.NegativeInfinity;
            foreach (var point in source)
            {
                if (point == null) throw new ProfileValidationException("Пустая точка в списке пикетов.");
                ProfileValidation.Range(point.Station, -1000000000, 1000000000, "Пикетаж");
                if (point.Station <= previous)
                    throw new ProfileValidationException("Для интерполяции пикеты должны строго возрастать; совпадающие пикеты недопустимы.");
                previous = point.Station;
                foreach (var get in Elevations) ProfileValidation.Elevation(get(point), "Интерполируемая отметка");
            }
        }

        private static void CheckSection(IList<SectionPoint> source)
        {
            if (source == null) throw new ProfileValidationException("Точки поперечника отсутствуют.");
            if (source.Count > 10000) throw new ProfileValidationException("Допустимо не более 10000 точек поперечника.");
            double previous = double.NegativeInfinity;
            foreach (var point in source)
            {
                if (point == null) throw new ProfileValidationException("Пустая точка в поперечнике.");
                ProfileValidation.Range(point.Offset, -1000000000, 1000000000, "Смещение поперечника");
                if (point.Offset <= previous)
                    throw new ProfileValidationException("Для интерполяции смещения должны строго возрастать; совпадающие смещения недопустимы.");
                previous = point.Offset;
                ProfileValidation.Elevation(point.Existing, "Фактическая отметка");
                ProfileValidation.Elevation(point.Design, "Проектная отметка");
            }
        }
    }
}
