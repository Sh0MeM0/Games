using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>
    /// Named longitudinal profiles and their own cross sections inside one project.
    /// The active profile uses the project's existing fields; the remaining profiles
    /// are stored exactly once, in their original tab order.
    /// </summary>
    public static class ProfileCollection
    {
        public static List<ProfileData> Enumerate(ProfileProject source)
        {
            return Snapshots(ProjectFile.Clone(source));
        }

        public static ProfileProject Select(ProfileProject source, string profileId)
        {
            var result = ProjectFile.Clone(source);
            var profiles = Snapshots(result);
            int index = Find(profiles, profileId);
            Activate(result, profiles, index);
            ProfileValidation.Check(result, false);
            return result;
        }

        public static ProfileProject Add(ProfileProject source, string name)
        {
            var result = ProjectFile.Clone(source);
            var profiles = Snapshots(result);
            name = NewName(name, profiles, null);
            if (profiles.Count >= 100)
                throw new ProfileValidationException("В одном проекте допустимо не более 100 продольных профилей.");
            var settings = result.Settings;
            profiles.Add(new ProfileData
            {
                ProfileId = Guid.NewGuid().ToString("N"), Name = name, Template = result.Template,
                Settings = new ProfileSettings
                {
                    Title = name,
                    HorizontalScale = settings.HorizontalScale, VerticalScale = settings.VerticalScale,
                    CrossHorizontalScale = settings.CrossHorizontalScale, CrossVerticalScale = settings.CrossVerticalScale,
                    MetresPerDrawingUnit = settings.MetresPerDrawingUnit,
                    TextHeightMm = settings.TextHeightMm, TextWidthFactor = settings.TextWidthFactor,
                    ShowEmptyRows = settings.ShowEmptyRows, Datum = null
                },
                Longitudinal = new List<LongitudinalPoint>(), Sections = new List<CrossSection>()
            });
            Activate(result, profiles, profiles.Count - 1);
            ProfileValidation.Check(result, false);
            return result;
        }

        public static ProfileProject Remove(ProfileProject source, string profileId)
        {
            var result = ProjectFile.Clone(source);
            var profiles = Snapshots(result);
            int index = Find(profiles, profileId);
            if (profiles.Count == 1)
                throw new ProfileValidationException("Нельзя удалить единственный профиль проекта. Создайте другой профиль или удалите весь проект.");
            string activeId = result.ProfileId;
            profiles.RemoveAt(index);
            int selected = string.Equals(activeId, profileId, StringComparison.OrdinalIgnoreCase)
                ? Math.Min(index, profiles.Count - 1) : Find(profiles, activeId);
            Activate(result, profiles, selected);
            ProfileValidation.Check(result, false);
            return result;
        }

        public static ProfileProject Rename(ProfileProject source, string profileId, string name)
        {
            var result = ProjectFile.Clone(source);
            var profiles = Snapshots(result);
            int index = Find(profiles, profileId);
            name = NewName(name, profiles, profiles[index].ProfileId);
            var renamed = profiles[index];
            if (renamed.Settings.Title == renamed.Name || renamed.Settings.Title == "ПРОДОЛЬНЫЙ ПРОФИЛЬ")
                renamed.Settings.Title = name;
            renamed.Name = name;
            Activate(result, profiles, result.ActiveProfileIndex);
            ProfileValidation.Check(result, false);
            return result;
        }

        private static List<ProfileData> Snapshots(ProfileProject owned)
        {
            var profiles = new List<ProfileData>(owned.AdditionalProfiles);
            if (owned.ActiveProfileIndex < 0 || owned.ActiveProfileIndex > profiles.Count)
                throw new ProfileValidationException("Некорректный порядок вкладок продольных профилей.");
            profiles.Insert(owned.ActiveProfileIndex, new ProfileData
            {
                ProfileId = owned.ProfileId, Name = owned.ProfileName,
                Template = owned.Template, Settings = owned.Settings,
                Longitudinal = owned.Longitudinal, Sections = owned.Sections
            });
            return profiles;
        }

        private static int Find(List<ProfileData> profiles, string profileId)
        {
            int index = profiles.FindIndex(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ProfileValidationException("Выбранная вкладка продольного профиля отсутствует в проекте.");
            return index;
        }

        private static string NewName(string name, List<ProfileData> profiles, string renamedId)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Any(char.IsControl))
                throw new ProfileValidationException("Введите название профиля от 1 до 200 символов в одну строку.");
            if (profiles.Any(profile => !string.Equals(profile.ProfileId, renamedId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ProfileValidationException("В проекте уже есть профиль с таким названием.");
            return name;
        }

        private static void Activate(ProfileProject result, List<ProfileData> profiles, int index)
        {
            var selected = profiles[index];
            result.ProfileId = selected.ProfileId;
            result.ProfileName = selected.Name;
            result.ActiveProfileIndex = index;
            result.Template = selected.Template;
            result.Settings = selected.Settings;
            result.Longitudinal = selected.Longitudinal;
            result.Sections = selected.Sections;
            result.AdditionalProfiles = profiles.Where((profile, position) => position != index).ToList();
        }
    }
}
