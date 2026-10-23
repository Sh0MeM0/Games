using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>Units of the picked source geometry, independent of profile output scales.</summary>
    public sealed class SurveySourceUnits
    {
        public double MetresPerDrawingUnit { get; set; } = 1;

        public static SurveySourceUnits ModelMetres() => new SurveySourceUnits();
        public static SurveySourceUnits PaperMillimetres(double scale)
        {
            ProfileValidation.Range(scale, 1, 1000000, "Масштаб исходного плана");
            return Custom(scale / 1000);
        }
        public static SurveySourceUnits Custom(double metresPerUnit)
        {
            var result = new SurveySourceUnits { MetresPerDrawingUnit = metresPerUnit };
            result.Validate();
            return result;
        }
        public static SurveySourceUnits Calibrated(Point2 first, Point2 second, double realMetres)
        {
            ProfileValidation.Range(realMetres, 0.000000001, 1000000000, "Известное расстояние, м");
            double picked = DrawingSurvey.PlanarDistance(first, second);
            if (picked <= 0) throw new ProfileValidationException("Для калибровки выберите две разные точки.");
            return Custom(realMetres / picked);
        }
        internal void Validate() => ProfileValidation.Range(MetresPerDrawingUnit, 0.000000000001, 1000000000000, "Метры в единице исходной геометрии");
    }

    public sealed class LongitudinalSurveyPick
    {
        public Point2 Position { get; set; }
        public LongitudinalPoint Values { get; set; } = new LongitudinalPoint();
    }

    public sealed class SectionSurveyPick
    {
        public Point2 Position { get; set; }
        public SectionPoint Values { get; set; } = new SectionPoint();
    }

    /// <summary>
    /// Converts explicit XY picks into measured chain distances. Text positions
    /// and output drawing scales are never used. Entered elevations are already
    /// metres and retain their original precision and nulls.
    /// </summary>
    public static class DrawingSurvey
    {
        public static List<LongitudinalPoint> BuildLongitudinal(IList<LongitudinalSurveyPick> picks,
            double startStation, SurveySourceUnits units)
        {
            CheckPicks(picks == null ? -1 : picks.Count, units);
            ProfileValidation.Range(startStation, -1000000000, 1000000000, "Начальный пикет, м");
            if (picks.Any(pick => pick == null || pick.Values == null))
                throw new ProfileValidationException("В захвате отсутствует точка или её данные.");
            // Use the strict project clone to validate every supplied field while
            // leaving the capture buffers untouched.
            var rows = ProjectFile.Clone(new ProfileProject { Longitudinal = picks.Select(pick => pick.Values).ToList() }).Longitudinal;
            double station = startStation;
            for (int index = 0; index < picks.Count; index++)
            {
                CheckPosition(picks[index].Position);
                if (index > 0)
                {
                    double next = station + Segment(picks[index - 1].Position, picks[index].Position, units);
                    if (next <= station) throw new ProfileValidationException("Расстояние слишком мало для данного пикетажа; проверьте единицы исходной геометрии.");
                    station = next;
                }
                ProfileValidation.Range(station, -1000000000, 1000000000, "Пикет по измеренной цепочке");
                rows[index].Station = station;
            }
            return rows;
        }

        public static CrossSection BuildSection(IList<SectionSurveyPick> picks, SurveySourceUnits units,
            double? station = null, string name = "Промер")
        {
            CheckPicks(picks == null ? -1 : picks.Count, units);
            if (picks.Any(pick => pick == null || pick.Values == null))
                throw new ProfileValidationException("В захвате отсутствует точка или её данные.");
            var section = new CrossSection
            {
                Name = name, Station = station, UsePointDistances = true,
                Points = picks.Select(pick => new SectionPoint
                {
                    Existing = pick.Values.Existing, Design = pick.Values.Design,
                    Label = pick.Values.Label, Role = pick.Values.Role
                }).ToList()
            };
            double offset = 0;
            for (int index = 0; index < picks.Count; index++)
            {
                CheckPosition(picks[index].Position);
                double distance = index == 0 ? 0 : Segment(picks[index - 1].Position, picks[index].Position, units);
                double next = offset + distance;
                if (index > 0 && next <= offset) throw new ProfileValidationException("Расстояние слишком мало для данного промера; проверьте единицы исходной геометрии.");
                offset = next;
                ProfileValidation.Range(offset, 0, 1000000000, "Суммарная ширина промера, м");
                section.Points[index].Offset = offset;
                section.Points[index].DistanceFromPrevious = distance;
            }
            if (section.Points.All(point => point.Role == SectionPointRole.Regular))
            {
                section.Points[0].Role = SectionPointRole.LeftBank;
                if (section.Points.Count > 1) section.Points[section.Points.Count - 1].Role = SectionPointRole.RightBank;
                var interior = section.Points.Select((point, index) => new { Point = point, Index = index })
                    .Where(item => item.Index > 0 && item.Index < section.Points.Count - 1 && item.Point.Existing.HasValue)
                    .OrderBy(item => item.Point.Existing.Value).ThenBy(item => item.Index).FirstOrDefault();
                if (interior != null) interior.Point.Role = SectionPointRole.Bottom;
            }
            CheckUniqueRoles(section);
            section.LeftBank = RoleElevation(section, SectionPointRole.LeftBank);
            section.ExistingBottom = RoleElevation(section, SectionPointRole.Bottom);
            section.RightBank = RoleElevation(section, SectionPointRole.RightBank);
            int bottom = section.Points.FindIndex(point => point.Role == SectionPointRole.Bottom);
            if (bottom >= 0) section.ProjectAnchorIndex = bottom;
            section.DesignMode = section.Points.Any(point => point.Design.HasValue) ? SectionDesignMode.Points : SectionDesignMode.None;
            return ProjectFile.Clone(new ProfileProject { Sections = new List<CrossSection> { section } }).Sections[0];
        }

        /// <summary>Overlay only supplied values at exact numeric stations; nulls never erase existing input.</summary>
        public static ProfileProject MergeLongitudinal(ProfileProject source, IList<LongitudinalPoint> imported)
        {
            var incoming = ProjectSynchronization.SortLongitudinal(imported);
            var result = ProjectFile.Clone(source);
            var previous = ProjectSynchronization.SortLongitudinal(result.Longitudinal);
            result.Longitudinal = ProjectSynchronization.SortLongitudinal(result.Longitudinal);
            var rows = result.Longitudinal.ToDictionary(point => point.Station);
            foreach (var entry in incoming)
            {
                LongitudinalPoint row;
                if (!rows.TryGetValue(entry.Station, out row))
                {
                    row = new LongitudinalPoint { Station = entry.Station };
                    rows.Add(entry.Station, row);
                }
                MergeEntered(row, entry);
            }
            result.Longitudinal = rows.Values.OrderBy(point => point.Station).ToList();
            result = ProjectSynchronization.RefreshFromLongitudinal(result, previous);
            // Identical re-entered values are still deliberate shared-field edits,
            // including when an old unlinked cross value differs from the master.
            ProjectSynchronization.ApplyEnteredLongitudinal(result, incoming);
            ProfileValidation.Check(result, false);
            return result;
        }

        /// <summary>Append captured drafts atomically. Unsited measurements remain local until a station is assigned.</summary>
        public static ProfileProject AppendSections(ProfileProject source, IList<CrossSection> drafts)
        {
            if (drafts == null) throw new ProfileValidationException("Список захваченных поперечников отсутствует.");
            var result = ProjectFile.Clone(source);
            var incoming = ProjectFile.Clone(new ProfileProject { Sections = drafts.ToList() }).Sections;
            foreach (var section in incoming)
            {
                CheckUniqueRoles(section);
                // Append is a new measured section, never an overwrite by identity.
                if (result.Sections.Any(existing => existing.SectionId == section.SectionId))
                    throw new ProfileValidationException("Захваченный поперечник уже добавлен в проект. Создайте новую копию с отдельным идентификатором.");
                double? station = section.Station;
                section.Station = null;
                section.UseLongitudinalProjectBottom = section.UseLongitudinalWater = false;
                foreach (var point in section.Points) point.UseLongitudinalElevation = false;
                result.Sections.Add(section);
                if (station.HasValue)
                {
                    var assigned = ProfileInterpolation.CloneSection(section);
                    assigned.Station = station;
                    result = ProjectSynchronization.CommitSection(result, result.Sections.Count - 1, section, assigned);
                }
            }
            ProfileValidation.Check(result, false);
            return result;
        }

        public static double PlanarDistance(Point2 first, Point2 second)
        {
            CheckPosition(first); CheckPosition(second);
            double dx = second.X - first.X, dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        internal static void CheckUniqueRoles(CrossSection section)
        {
            foreach (var group in section.Points.Where(point => point.Role != SectionPointRole.Regular).GroupBy(point => point.Role))
                if (group.Count() > 1)
                    throw new ProfileValidationException("Поперечник «" + section.Name + "»: каждому берегу и дну назначьте только одну опорную точку.");
        }

        private static double? RoleElevation(CrossSection section, SectionPointRole role) =>
            section.Points.Where(point => point.Role == role).Select(point => point.Existing).FirstOrDefault();

        private static void CheckPicks(int count, SurveySourceUnits units)
        {
            if (count < 1 || count > 10000) throw new ProfileValidationException("Выберите от 1 до 10000 точек исходной геометрии.");
            if (units == null) throw new ProfileValidationException("Укажите единицы исходной геометрии.");
            units.Validate();
        }

        private static void CheckPosition(Point2 point)
        {
            ProfileValidation.Range(point.X, -1000000000000, 1000000000000, "Координата X исходной точки");
            ProfileValidation.Range(point.Y, -1000000000000, 1000000000000, "Координата Y исходной точки");
        }

        private static double Segment(Point2 first, Point2 second, SurveySourceUnits units)
        {
            double distance = PlanarDistance(first, second) * units.MetresPerDrawingUnit;
            if (distance <= 0) throw new ProfileValidationException("Соседние выбранные точки совпадают; выберите разные точки промера.");
            ProfileValidation.Range(distance, 0, 1000000000, "Измеренное расстояние, м");
            return distance;
        }

        private static void MergeEntered(LongitudinalPoint target, LongitudinalPoint entered)
        {
            if (entered.Ground.HasValue) target.Ground = entered.Ground;
            if (entered.LeftBank.HasValue) target.LeftBank = entered.LeftBank;
            if (entered.RightBank.HasValue) target.RightBank = entered.RightBank;
            if (entered.ExistingBottom.HasValue) target.ExistingBottom = entered.ExistingBottom;
            if (entered.Water.HasValue) target.Water = entered.Water;
            if (entered.ProjectBottom.HasValue) target.ProjectBottom = entered.ProjectBottom;
            if (entered.BottomWidth.HasValue) target.BottomWidth = entered.BottomWidth;
            if (entered.LeftSlope.HasValue) target.LeftSlope = entered.LeftSlope;
            if (entered.RightSlope.HasValue) target.RightSlope = entered.RightSlope;
            if (entered.ProjectGround.HasValue) target.ProjectGround = entered.ProjectGround;
            if (entered.PipeInvert.HasValue) target.PipeInvert = entered.PipeInvert;
            if (entered.PeatDepth.HasValue) target.PeatDepth = entered.PeatDepth;
            if (!string.IsNullOrWhiteSpace(entered.PointName)) target.PointName = entered.PointName;
            if (!string.IsNullOrWhiteSpace(entered.PipeDescription)) target.PipeDescription = entered.PipeDescription;
            if (!string.IsNullOrWhiteSpace(entered.Foundation)) target.Foundation = entered.Foundation;
        }
    }
}
