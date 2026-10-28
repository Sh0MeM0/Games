using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>Distance-weighted interpolation of missing elevations and channel dimensions; input anchors are never changed.</summary>
    public static class ProfileInterpolation
    {
        private static readonly Func<LongitudinalPoint, double?>[] InterpolatedValues =
        {
            p => p.Ground, p => p.LeftBank, p => p.RightBank,
            p => p.ExistingBottom, p => p.Water, p => p.ProjectBottom,
            p => p.ProjectGround, p => p.PipeInvert,
            p => p.BottomWidth, p => p.LeftSlope, p => p.RightSlope
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
            for (int column = 0; column < InterpolatedValues.Length; column++)
            {
                var get = InterpolatedValues[column];
                int previous = -1, penultimate = -1;
                for (int next = 0; next < source.Count; next++)
                {
                    if (!get(source[next]).HasValue) continue;
                    if (previous >= 0)
                        for (int index = previous + 1; index < next; index++)
                            Set(result[index], column, Between(source[previous].Station, get(source[previous]).Value,
                                source[next].Station, get(source[next]).Value, source[index].Station));
                    penultimate = previous;
                    previous = next;
                }
                if (ExtendsEnd(column) && previous >= 0)
                    for (int index = previous + 1; index < source.Count; index++)
                        Set(result[index], column, EndValue(source, get, penultimate, previous, source[index].Station));
            }
            return result;
        }

        public static LongitudinalPoint SampleLongitudinal(IList<LongitudinalPoint> source, double station)
        {
            CheckLongitudinal(source);
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж");
            var exact = source.FirstOrDefault(p => p.Station == station);
            var sample = exact == null ? new LongitudinalPoint { Station = station } : Copy(exact);
            for (int column = 0; column < InterpolatedValues.Length; column++)
            {
                var get = InterpolatedValues[column];
                double? value = ValueAt(source, p => p.Station, get, station);
                if (!value.HasValue && ExtendsEnd(column))
                {
                    int last = -1, previous = -1;
                    for (int index = 0; index < source.Count; index++)
                        if (get(source[index]).HasValue) { previous = last; last = index; }
                    if (last >= 0 && station > source[last].Station)
                        value = EndValue(source, get, previous, last, station);
                }
                Set(sample, column, value);
            }
            return sample;
        }

        /// <summary>
        /// Suggested channel dimensions for a newly bound section. Each field has
        /// independent anchors; outside them use the nearest known value rather
        /// than extrapolating a width or slope. This does not change raw table cells.
        /// </summary>
        public static LongitudinalPoint SampleDesignParameters(IList<LongitudinalPoint> source, double station)
        {
            CheckLongitudinal(source);
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж");
            return new LongitudinalPoint
            {
                Station = station,
                BottomWidth = NearestOrBetween(source, point => point.BottomWidth, station),
                LeftSlope = NearestOrBetween(source, point => point.LeftSlope, station),
                RightSlope = NearestOrBetween(source, point => point.RightSlope, station)
            };
        }

        private static double? NearestOrBetween(IList<LongitudinalPoint> source,
            Func<LongitudinalPoint, double?> value, double station)
        {
            var known = source.Where(point => value(point).HasValue).ToList();
            if (known.Count == 0) return null;
            if (station <= known[0].Station) return value(known[0]);
            if (station >= known[known.Count - 1].Station) return value(known[known.Count - 1]);
            return ValueAt(known, point => point.Station, value, station);
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
            if (section.ProjectBottom.HasValue)
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
                SectionId = source.SectionId, Name = source.Name, Station = source.Station, DesignMode = source.DesignMode,
                UsePointDistances = source.UsePointDistances, ProjectAnchorIndex = source.ProjectAnchorIndex,
                UseLongitudinalProjectBottom = source.UseLongitudinalProjectBottom, UseLongitudinalWater = source.UseLongitudinalWater,
                LeftBank = source.LeftBank, RightBank = source.RightBank, ExistingBottom = source.ExistingBottom,
                Water = source.Water, ProjectBottom = source.ProjectBottom, BottomWidth = source.BottomWidth,
                LeftSlope = source.LeftSlope, RightSlope = source.RightSlope, CenterOffset = source.CenterOffset,
                SiltThickness = source.SiltThickness, SiltLeft = source.SiltLeft, SiltRight = source.SiltRight,
                Points = source.Points.Select(point => new SectionPoint
                {
                    Offset = point.Offset, Existing = point.Existing, Design = point.Design,
                    Label = point.Label, Role = point.Role, DistanceFromPrevious = point.DistanceFromPrevious,
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

        /// <summary>
        /// Gives a newly enabled project an editable survey column. Existing user
        /// anchors are retained. The true automatic bottom center is kept even
        /// between measured points; only a coincident column becomes a real anchor.
        /// Incomplete distances permit an anchor but leave its offset unresolved.
        /// </summary>
        public static void EnsureProjectAnchor(CrossSection section)
        {
            if (section == null || section.Points == null || section.Points.Any(point => point == null))
                throw new ProfileValidationException("Поперечник или его точки отсутствуют.");
            if (section.Points.Count == 0) return;
            if (section.ProjectAnchorIndex.HasValue && (section.ProjectAnchorIndex.Value < 0 || section.ProjectAnchorIndex.Value >= section.Points.Count))
                throw new ProfileValidationException("Выбранная проектная опорная точка отсутствует в поперечнике.");
            var measured = section.Points.Select(point => new SectionPoint { Offset = point.Offset, Existing = point.Existing }).ToList();
            bool distancesKnown = true;
            if (section.UsePointDistances)
            {
                double offset = measured[0].Offset;
                for (int index = 1; index < measured.Count; index++)
                {
                    double? distance = section.Points[index].DistanceFromPrevious;
                    if (!distance.HasValue || !NumberParser.Finite(distance.Value) || distance.Value <= 0)
                    { distancesKnown = false; break; }
                    offset += distance.Value;
                    if (!NumberParser.Finite(offset) || offset <= measured[index - 1].Offset)
                    { distancesKnown = false; break; }
                    measured[index].Offset = offset;
                }
            }
            else
                distancesKnown = measured.Select((point, index) => index == 0 || point.Offset > measured[index - 1].Offset).All(valid => valid);
            if (!section.ProjectAnchorIndex.HasValue)
            {
                int anchor = -1;
                if (distancesKnown && measured.Any(point => point.Existing.HasValue))
                {
                    double center = FindCenterOffset(measured);
                    section.CenterOffset = center;
                    anchor = measured.FindIndex(point => point.Offset == center);
                    // The editor may display the scalar under its nearest column,
                    // but that visual fallback must not move the real geometry.
                    if (anchor < 0) return;
                }
                else
                {
                    anchor = section.Points.FindIndex(point => point.Role == SectionPointRole.Bottom);
                    if (anchor < 0)
                        anchor = section.Points.Select((point, index) => new { Point = point, Index = index })
                            .Where(item => item.Point.Existing.HasValue).OrderBy(item => item.Point.Existing.Value)
                            .ThenBy(item => item.Index).Select(item => item.Index).DefaultIfEmpty(-1).First();
                }
                if (anchor < 0) return;
                section.ProjectAnchorIndex = anchor;
            }
            if (distancesKnown) section.CenterOffset = measured[section.ProjectAnchorIndex.Value].Offset;
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

        // Continue only the bottom/design elevations beyond their last raw anchor.
        // Surveyed terrain, water and physical dimensions retain their existing rules.
        private static bool ExtendsEnd(int column) => column == 3 || column == 5 || column == 6 || column == 7;

        private static double EndValue(IList<LongitudinalPoint> source, Func<LongitudinalPoint, double?> get,
            int previous, int last, double station)
        {
            double value = previous < 0 ? get(source[last]).Value :
                Between(source[previous].Station, get(source[previous]).Value,
                    source[last].Station, get(source[last]).Value, station);
            ProfileValidation.Elevation(value, "Экстраполированная отметка");
            return value;
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
                case 8: point.BottomWidth = value; break;
                case 9: point.LeftSlope = value; break;
                case 10: point.RightSlope = value; break;
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
                for (int column = 0; column < InterpolatedValues.Length; column++)
                {
                    double? value = InterpolatedValues[column](point);
                    if (column < 8) ProfileValidation.Elevation(value, "Интерполируемая отметка");
                    else if (value.HasValue)
                        ProfileValidation.Range(value.Value, 0.000001, 1000000, "Ширина дна или заложение откоса");
                }
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
