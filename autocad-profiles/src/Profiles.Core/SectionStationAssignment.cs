using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>Assigns a measured section to a longitudinal station without losing its geometry.</summary>
    public static class SectionStationAssignment
    {
        public static bool TryParseName(string name, out double station)
        {
            station = 0;
            string text = (name ?? "").Trim();
            if (text.StartsWith("ПК", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2).Trim();
            try
            {
                station = NumberParser.ParseRequired(text, "Пикет");
                ProfileValidation.Range(station, -1000000000, 1000000000, "Пикет");
                return true;
            }
            catch (ProfileValidationException) { return false; }
        }

        public static ProfileProject Assign(ProfileProject source, int sectionIndex, double station)
        {
            CheckTarget(source, sectionIndex, station);
            var previous = source.Sections[sectionIndex];
            var assigned = ProfileInterpolation.CloneSection(previous);
            assigned.Station = station;
            assigned.Name = "ПК " + ProfileEngine.Station(station);
            return ProjectSynchronization.CommitSection(source, sectionIndex, previous, assigned);
        }

        public static List<string> ConflictingFields(ProfileProject source, int sectionIndex, double station)
        {
            CheckTarget(source, sectionIndex, station);
            var section = source.Sections[sectionIndex];
            var target = source.Longitudinal.SingleOrDefault(point => point.Station == station);
            var conflicts = new List<string>();
            if (target == null || section.Station == station) return conflicts;
            AddConflict(conflicts, "левый берег", section.Points.FirstOrDefault(p => p.Role == SectionPointRole.LeftBank)?.Existing ?? section.LeftBank, target.LeftBank);
            AddConflict(conflicts, "дно фактическое", section.Points.FirstOrDefault(p => p.Role == SectionPointRole.Bottom)?.Existing ?? section.ExistingBottom, target.ExistingBottom);
            AddConflict(conflicts, "правый берег", section.Points.FirstOrDefault(p => p.Role == SectionPointRole.RightBank)?.Existing ?? section.RightBank, target.RightBank);
            AddConflict(conflicts, "урез воды", section.Water, target.Water);
            AddConflict(conflicts, "дно проектное", section.ProjectBottom, target.ProjectBottom);
            AddConflict(conflicts, "ширина дна", section.BottomWidth, target.BottomWidth);
            AddConflict(conflicts, "левый откос", section.LeftSlope, target.LeftSlope);
            AddConflict(conflicts, "правый откос", section.RightSlope, target.RightSlope);
            return conflicts;
        }

        private static void AddConflict(List<string> result, string caption, double? measured, double? existing)
        {
            if (measured.HasValue && existing.HasValue && measured.Value != existing.Value) result.Add(caption);
        }

        private static void CheckTarget(ProfileProject source, int index, double station)
        {
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикет");
            if (index < 0 || index >= source.Sections.Count)
                throw new ProfileValidationException("Сначала выберите промер в списке поперечников.");
            if (source.Sections.Where((section, i) => i != index).Any(section => section.Station == station))
                throw new ProfileValidationException("На этом пикете уже есть другой поперечник. Выберите другой пикет либо сначала снимите привязку существующего. Оба промера сохранены.");
        }
    }
}
