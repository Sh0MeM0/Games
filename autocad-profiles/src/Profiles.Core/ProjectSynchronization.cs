using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    public enum SharedProfileField
    {
        LeftBank, RightBank, ExistingBottom, Water, ProjectBottom,
        BottomWidth, LeftSlope, RightSlope
    }

    /// <summary>
    /// Explicit editor transactions for data shared by a longitudinal station and
    /// its cross sections. Every operation returns a deep copy; raw interpolated
    /// cells stay empty unless that particular field was deliberately edited.
    /// </summary>
    public static class ProjectSynchronization
    {
        private static readonly SharedProfileField[] Fields =
            (SharedProfileField[])Enum.GetValues(typeof(SharedProfileField));

        public static ProfileProject SetSharedValue(ProfileProject source, double station,
            SharedProfileField field, double? value)
        {
            CheckField(field);
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж");
            var result = CopySorted(source);
            var previous = SortLongitudinal(result.Longitudinal);
            var point = EnsureStation(result, station);
            Set(point, field, value);
            ProfileValidation.Check(result, false);
            Propagate(result, previous, new[] { field }, station);
            return result;
        }

        /// <summary>
        /// Use after an explicit longitudinal edit. Only changed fields and their
        /// changed interpolated values propagate; unrelated old manual data remains.
        /// </summary>
        public static ProfileProject RefreshFromLongitudinal(ProfileProject source,
            IList<LongitudinalPoint> previousLongitudinal)
        {
            var result = CopySorted(source);
            var previous = SortLongitudinal(previousLongitudinal);
            Propagate(result, previous, Fields, null);
            return result;
        }

        /// <summary>Safe on load: refresh existing links, preserving legacy manual fields.</summary>
        public static ProfileProject RefreshFromLongitudinal(ProfileProject source)
        {
            var result = CopySorted(source);
            foreach (var section in result.Sections)
            {
                if (!section.Station.HasValue) continue;
                RefreshExistingLinks(section, ProfileInterpolation.SampleLongitudinal(result.Longitudinal, section.Station.Value));
            }
            return result;
        }

        /// <summary>
        /// Commits an editor buffer against its previous snapshot. Unchanged shared
        /// fields are taken from the current project, so an old buffer cannot undo
        /// a newer edit elsewhere. Section order and all local survey data remain.
        /// </summary>
        public static ProfileProject CommitSection(ProfileProject source, int sectionIndex,
            CrossSection previous, CrossSection edited)
        {
            var result = CopySorted(source);
            if (sectionIndex < 0 || sectionIndex >= result.Sections.Count)
                throw new ProfileValidationException("Поперечник для сохранения отсутствует в проекте.");
            // Clone before inspection: neither edit buffer becomes part of the result.
            previous = ProfileInterpolation.CloneSection(previous);
            var pending = ProfileInterpolation.CloneSection(edited);
            var current = result.Sections[sectionIndex];
            var changes = new Dictionary<SharedProfileField, double?>();
            foreach (var field in Fields)
            {
                double? value;
                if (DetectChange(previous, pending, field, out value)) changes.Add(field, value);
            }

            var currentValues = ProfileInterpolation.CloneSection(current);
            if (currentValues.Station.HasValue)
                RefreshExistingLinks(currentValues, ProfileInterpolation.SampleLongitudinal(result.Longitudinal, currentValues.Station.Value));
            foreach (var field in Fields)
                if (!changes.ContainsKey(field)) MergeUnchanged(pending, currentValues, field);

            bool reassigned = pending.Station.HasValue && pending.Station != previous.Station;
            if (reassigned)
            {
                foreach (var field in Fields)
                {
                    if (changes.ContainsKey(field)) continue;
                    double? value = PendingValue(pending, field);
                    if (value.HasValue) changes.Add(field, value);
                }
            }
            result.Sections[sectionIndex] = pending;
            if (!pending.Station.HasValue)
            {
                foreach (var change in changes) Apply(pending, change.Key, change.Value, false);
                pending.UseLongitudinalProjectBottom = pending.UseLongitudinalWater = false;
                foreach (var point in pending.Points) point.UseLongitudinalElevation = false;
                ProfileValidation.Check(result, false);
                return result;
            }

            if (changes.Count == 0 && !reassigned)
            {
                ProfileValidation.Check(result, false);
                return result;
            }
            var oldLongitudinal = SortLongitudinal(result.Longitudinal);
            var anchor = EnsureStation(result, pending.Station.Value);
            foreach (var change in changes) Set(anchor, change.Key, change.Value);
            ProfileValidation.Check(result, false);
            Propagate(result, oldLongitudinal, changes.Keys, pending.Station.Value);
            return result;
        }

        /// <summary>Canonical numeric order; duplicates are errors rather than merged survey rows.</summary>
        public static List<LongitudinalPoint> SortLongitudinal(IList<LongitudinalPoint> source)
        {
            if (source == null) throw new ProfileValidationException("Список продольных точек отсутствует.");
            var result = ProjectFile.Clone(new ProfileProject { Longitudinal = source.ToList() }).Longitudinal;
            result = result.OrderBy(point => point.Station).ToList();
            for (int index = 1; index < result.Count; index++)
                if (result[index - 1].Station == result[index].Station)
                    throw new ProfileValidationException("Продольный профиль: пикет " + ProfileEngine.Station(result[index].Station)
                        + " повторяется. Удалите совпадающую строку или исправьте её пикетаж перед синхронизацией.");
            return result;
        }

        private static ProfileProject CopySorted(ProfileProject source)
        {
            var result = ProjectFile.Clone(source);
            result.Longitudinal = SortLongitudinal(result.Longitudinal);
            return result;
        }

        private static LongitudinalPoint EnsureStation(ProfileProject project, double station)
        {
            var point = project.Longitudinal.FirstOrDefault(value => value.Station == station);
            if (point != null) return point;
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж");
            point = new LongitudinalPoint { Station = station };
            project.Longitudinal.Add(point);
            project.Longitudinal.Sort((left, right) => left.Station.CompareTo(right.Station));
            return point;
        }

        private static void Propagate(ProfileProject result, IList<LongitudinalPoint> previous,
            IEnumerable<SharedProfileField> fields, double? forceStation)
        {
            var oldRows = previous.ToDictionary(point => point.Station);
            var newRows = result.Longitudinal.ToDictionary(point => point.Station);
            var changed = new Dictionary<SharedProfileField, HashSet<double>>();
            foreach (var field in fields)
            {
                var stations = new HashSet<double>();
                foreach (double station in oldRows.Keys.Union(newRows.Keys))
                {
                    LongitudinalPoint before, after;
                    oldRows.TryGetValue(station, out before);
                    newRows.TryGetValue(station, out after);
                    if (Get(before, field) != Get(after, field)) stations.Add(station);
                }
                if (forceStation.HasValue) stations.Add(forceStation.Value);
                if (stations.Count != 0) changed.Add(field, stations);
            }
            foreach (var section in result.Sections)
            {
                if (!section.Station.HasValue || changed.Count == 0) continue;
                var before = ProfileInterpolation.SampleLongitudinal(previous, section.Station.Value);
                var after = ProfileInterpolation.SampleLongitudinal(result.Longitudinal, section.Station.Value);
                foreach (var change in changed)
                    if (change.Value.Contains(section.Station.Value) || Get(before, change.Key) != Get(after, change.Key))
                        Apply(section, change.Key, Get(after, change.Key), true);
            }
        }

        private static bool DetectChange(CrossSection previous, CrossSection edited,
            SharedProfileField field, out double? value)
        {
            value = Get(edited, field);
            bool scalarChanged = Get(previous, field) != value;
            var role = Role(field);
            if (role.HasValue)
            {
                var before = previous.Points.Where(point => point.Role == role.Value).ToList();
                var after = edited.Points.Where(point => point.Role == role.Value).ToList();
                // Deleting a survey column is a local structural operation, not an explicit clearing of the shared elevation.
                if (after.Count != 0)
                {
                    bool listChanged = !before.Select(point => point.Existing).SequenceEqual(after.Select(point => point.Existing));
                    bool ambiguous = after.Select(point => point.Existing).Distinct().Count() > 1;
                    if (ambiguous && (listChanged || scalarChanged)) throw RoleConflict(edited, role.Value);
                    if (!ambiguous)
                    {
                        double? roleValue = after[0].Existing;
                        bool roleChanged = before.Count == 0 ? roleValue.HasValue
                            : before.Any(point => point.Existing != roleValue);
                        if (roleChanged)
                        {
                            if (scalarChanged && value != roleValue) throw ValueConflict(edited, field);
                            value = roleValue;
                            return true;
                        }
                    }
                }
            }
            if (field == SharedProfileField.ProjectBottom && ProfileInterpolation.EffectiveDesignMode(edited) == SectionDesignMode.Points)
            {
                double? anchorValue;
                if (TryAnchor(edited, out anchorValue))
                {
                    double? beforeValue;
                    bool sameAnchor = previous.ProjectAnchorIndex == edited.ProjectAnchorIndex
                        && ProfileInterpolation.EffectiveDesignMode(previous) == SectionDesignMode.Points
                        && TryAnchor(previous, out beforeValue);
                    bool anchorChanged = sameAnchor ? previous.Points[previous.ProjectAnchorIndex.Value].Design != anchorValue : anchorValue.HasValue;
                    if (anchorChanged)
                    {
                        if (scalarChanged && value != anchorValue) throw ValueConflict(edited, field);
                        value = anchorValue;
                        return true;
                    }
                }
            }
            return scalarChanged;
        }

        private static double? PendingValue(CrossSection section, SharedProfileField field)
        {
            var role = Role(field);
            if (role.HasValue)
            {
                var points = section.Points.Where(point => point.Role == role.Value).ToList();
                CheckRole(section, points, role.Value);
                if (points.Count > 0 && points[0].Existing.HasValue) return points[0].Existing;
            }
            double? anchor;
            if (field == SharedProfileField.ProjectBottom && ProfileInterpolation.EffectiveDesignMode(section) == SectionDesignMode.Points
                && TryAnchor(section, out anchor) && anchor.HasValue) return anchor;
            return Get(section, field);
        }

        private static void MergeUnchanged(CrossSection edited, CrossSection current, SharedProfileField field)
        {
            Set(edited, field, Get(current, field));
            if (field == SharedProfileField.Water) edited.UseLongitudinalWater = current.UseLongitudinalWater;
            if (field == SharedProfileField.ProjectBottom)
            {
                edited.UseLongitudinalProjectBottom = current.UseLongitudinalProjectBottom;
                double? anchor;
                if (ProfileInterpolation.EffectiveDesignMode(edited) == SectionDesignMode.Points && TryAnchor(current, out anchor)
                    && edited.ProjectAnchorIndex == current.ProjectAnchorIndex && edited.ProjectAnchorIndex.Value < edited.Points.Count)
                    edited.Points[edited.ProjectAnchorIndex.Value].Design = anchor;
            }
            var role = Role(field);
            if (!role.HasValue) return;
            var before = current.Points.Where(point => point.Role == role.Value).ToList();
            var after = edited.Points.Where(point => point.Role == role.Value).ToList();
            if (before.Count == 0) return;
            if (before.Select(point => point.Existing).Distinct().Count() == 1)
            {
                foreach (var point in after)
                {
                    point.Existing = before[0].Existing;
                    point.UseLongitudinalElevation = before.All(item => item.UseLongitudinalElevation);
                }
            }
            else if (before.Count == after.Count)
                for (int index = 0; index < before.Count; index++)
                {
                    after[index].Existing = before[index].Existing;
                    after[index].UseLongitudinalElevation = before[index].UseLongitudinalElevation;
                }
        }

        private static void RefreshExistingLinks(CrossSection section, LongitudinalPoint station)
        {
            if (section.UseLongitudinalWater) section.Water = station.Water;
            if (section.UseLongitudinalProjectBottom)
            {
                section.ProjectBottom = station.ProjectBottom;
                SetProjectAnchor(section, station.ProjectBottom);
            }
            foreach (var point in section.Points)
            {
                if (!point.UseLongitudinalElevation) continue;
                if (point.Role == SectionPointRole.LeftBank) point.Existing = section.LeftBank = station.LeftBank;
                if (point.Role == SectionPointRole.RightBank) point.Existing = section.RightBank = station.RightBank;
                if (point.Role == SectionPointRole.Bottom) point.Existing = section.ExistingBottom = station.ExistingBottom;
            }
        }

        private static void Apply(CrossSection section, SharedProfileField field, double? value, bool linked)
        {
            var role = Role(field);
            if (role.HasValue)
            {
                var points = section.Points.Where(point => point.Role == role.Value).ToList();
                CheckRole(section, points, role.Value);
                foreach (var point in points)
                {
                    point.Existing = value;
                    point.UseLongitudinalElevation = linked;
                }
            }
            Set(section, field, value);
            if (field == SharedProfileField.Water) section.UseLongitudinalWater = linked;
            if (field == SharedProfileField.ProjectBottom)
            {
                section.UseLongitudinalProjectBottom = linked;
                SetProjectAnchor(section, value);
            }
        }

        private static void SetProjectAnchor(CrossSection section, double? value)
        {
            if (ProfileInterpolation.EffectiveDesignMode(section) == SectionDesignMode.Points
                && section.ProjectAnchorIndex.HasValue && section.ProjectAnchorIndex.Value >= 0
                && section.ProjectAnchorIndex.Value < section.Points.Count)
                section.Points[section.ProjectAnchorIndex.Value].Design = value;
        }

        private static bool TryAnchor(CrossSection section, out double? value)
        {
            value = null;
            if (!section.ProjectAnchorIndex.HasValue || section.ProjectAnchorIndex.Value < 0
                || section.ProjectAnchorIndex.Value >= section.Points.Count) return false;
            value = section.Points[section.ProjectAnchorIndex.Value].Design;
            return true;
        }

        private static void CheckRole(CrossSection section, IList<SectionPoint> points, SectionPointRole role)
        {
            if (points.Select(point => point.Existing).Distinct().Count() > 1) throw RoleConflict(section, role);
        }

        private static ProfileValidationException RoleConflict(CrossSection section, SectionPointRole role) =>
            new ProfileValidationException("Поперечник «" + section.Name + "»: нескольким точкам назначена одна роль (" + role
                + "), но их отметки различаются. Оставьте одну опорную точку для этой роли перед синхронизацией.");

        private static ProfileValidationException ValueConflict(CrossSection section, SharedProfileField field) =>
            new ProfileValidationException("Поперечник «" + section.Name + "»: поле " + field
                + " и соответствующая опорная точка изменены по-разному. Укажите одну общую отметку.");

        private static SectionPointRole? Role(SharedProfileField field)
        {
            switch (field)
            {
                case SharedProfileField.LeftBank: return SectionPointRole.LeftBank;
                case SharedProfileField.RightBank: return SectionPointRole.RightBank;
                case SharedProfileField.ExistingBottom: return SectionPointRole.Bottom;
                default: return null;
            }
        }

        private static void CheckField(SharedProfileField field)
        {
            if (!Enum.IsDefined(typeof(SharedProfileField), field)) throw new ProfileValidationException("Неизвестное общее поле профиля.");
        }

        private static double? Get(LongitudinalPoint point, SharedProfileField field)
        {
            if (point == null) return null;
            switch (field)
            {
                case SharedProfileField.LeftBank: return point.LeftBank;
                case SharedProfileField.RightBank: return point.RightBank;
                case SharedProfileField.ExistingBottom: return point.ExistingBottom;
                case SharedProfileField.Water: return point.Water;
                case SharedProfileField.ProjectBottom: return point.ProjectBottom;
                case SharedProfileField.BottomWidth: return point.BottomWidth;
                case SharedProfileField.LeftSlope: return point.LeftSlope;
                case SharedProfileField.RightSlope: return point.RightSlope;
                default: throw new ProfileValidationException("Неизвестное общее поле профиля.");
            }
        }

        private static double? Get(CrossSection section, SharedProfileField field)
        {
            switch (field)
            {
                case SharedProfileField.LeftBank: return section.LeftBank;
                case SharedProfileField.RightBank: return section.RightBank;
                case SharedProfileField.ExistingBottom: return section.ExistingBottom;
                case SharedProfileField.Water: return section.Water;
                case SharedProfileField.ProjectBottom: return section.ProjectBottom;
                case SharedProfileField.BottomWidth: return section.BottomWidth;
                case SharedProfileField.LeftSlope: return section.LeftSlope;
                case SharedProfileField.RightSlope: return section.RightSlope;
                default: throw new ProfileValidationException("Неизвестное общее поле профиля.");
            }
        }

        private static void Set(LongitudinalPoint point, SharedProfileField field, double? value)
        {
            switch (field)
            {
                case SharedProfileField.LeftBank: point.LeftBank = value; break;
                case SharedProfileField.RightBank: point.RightBank = value; break;
                case SharedProfileField.ExistingBottom: point.ExistingBottom = value; break;
                case SharedProfileField.Water: point.Water = value; break;
                case SharedProfileField.ProjectBottom: point.ProjectBottom = value; break;
                case SharedProfileField.BottomWidth: point.BottomWidth = value; break;
                case SharedProfileField.LeftSlope: point.LeftSlope = value; break;
                case SharedProfileField.RightSlope: point.RightSlope = value; break;
                default: throw new ProfileValidationException("Неизвестное общее поле профиля.");
            }
        }

        private static void Set(CrossSection section, SharedProfileField field, double? value)
        {
            switch (field)
            {
                case SharedProfileField.LeftBank: section.LeftBank = value; break;
                case SharedProfileField.RightBank: section.RightBank = value; break;
                case SharedProfileField.ExistingBottom: section.ExistingBottom = value; break;
                case SharedProfileField.Water: section.Water = value; break;
                case SharedProfileField.ProjectBottom: section.ProjectBottom = value; break;
                case SharedProfileField.BottomWidth: section.BottomWidth = value; break;
                case SharedProfileField.LeftSlope: section.LeftSlope = value; break;
                case SharedProfileField.RightSlope: section.RightSlope = value; break;
                default: throw new ProfileValidationException("Неизвестное общее поле профиля.");
            }
        }
    }
}
