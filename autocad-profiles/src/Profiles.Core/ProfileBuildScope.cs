using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>One section in its own local coordinates, with the suggested offset for a new batch.</summary>
    public sealed class SectionDrawingPlan
    {
        public string SectionId { get; }
        public DrawingPlan Plan { get; }
        public double OffsetY { get; }

        internal SectionDrawingPlan(string sectionId, DrawingPlan plan, double offsetY)
        { SectionId = sectionId; Plan = plan; OffsetY = offsetY; }
    }

    public sealed class ProfileDrawingPlan
    {
        public string ProfileId { get; internal set; }
        public string ProfileName { get; internal set; }
        public string SectionId { get; internal set; }
        public DrawingPlan Plan { get; internal set; }
        public ProfileSettings Settings { get; internal set; }
        public double OffsetY { get; internal set; }
    }

    /// <summary>Drawing scope never removes unrelated data from the saved project.</summary>
    public static class ProfileBuildScope
    {
        public static DrawingPlan Build(ProfileProject project, string selectedSectionId, bool allSections, bool allProfiles = false)
        {
            if (project != null && project.Kind == ProfileKind.Longitudinal && allProfiles)
            {
                var grouped = Groups(project, selectedSectionId, allSections, true);
                var combined = new DrawingPlan { Datum = grouped[0].Plan.Datum };
                foreach (var group in grouped)
                {
                    foreach (var entity in group.Plan.Entities) ProfileEngine.Shift(entity, group.OffsetY);
                    combined.Entities.AddRange(group.Plan.Entities);
                    combined.Warnings.AddRange(group.Plan.Warnings);
                }
                return combined;
            }
            return ProfileEngine.Build(SelectedProject(project, selectedSectionId, allSections));
        }

        public static IReadOnlyList<ProfileDrawingPlan> Groups(ProfileProject project, string selectedSectionId, bool allSections, bool allProfiles = false)
        {
            if (project == null) throw new ProfileValidationException("Проект отсутствует.");
            var result = new List<ProfileDrawingPlan>();
            if (project.Kind == ProfileKind.CrossSection)
            {
                foreach (var section in Sections(project, selectedSectionId, allSections))
                    result.Add(new ProfileDrawingPlan
                    {
                        ProfileId = project.ProfileId, ProfileName = project.ProfileName, SectionId = section.SectionId,
                        Plan = section.Plan, OffsetY = section.OffsetY, Settings = ProfileScales.ForKind(project.Settings, project.Kind)
                    });
                return result;
            }
            var profiles = allProfiles ? ProfileCollection.Enumerate(project).Select(profile => new ProfileProject
            {
                ProjectId = project.ProjectId, Name = project.Name, Kind = ProfileKind.Longitudinal,
                ProfileId = profile.ProfileId, ProfileName = profile.Name, Template = profile.Template,
                Settings = profile.Settings, Longitudinal = profile.Longitudinal, Sections = profile.Sections
            }).ToList() : new List<ProfileProject> { project };
            double? previousBottom = null;
            int entityCount = 0;
            foreach (var profile in profiles)
            {
                var plan = ProfileEngine.Build(profile);
                var settings = ProfileScales.ForKind(profile.Settings, ProfileKind.Longitudinal);
                double layout = settings.HorizontalScale / (1000 * settings.MetresPerDrawingUnit) * Math.Max(1, settings.TextHeightMm / 2.5);
                double minimum = ProfileEngine.Bounds(plan.Entities, false), maximum = ProfileEngine.Bounds(plan.Entities, true);
                double offset = previousBottom.HasValue ? previousBottom.Value - 20 * layout - maximum : 0;
                if (!NumberParser.Finite(minimum + offset) || Math.Abs(minimum + offset) > 1e12 ||
                    !NumberParser.Finite(maximum + offset) || Math.Abs(maximum + offset) > 1e12)
                    throw new ProfileValidationException("Размер группы профилей слишком велик; постройте профили по отдельности.");
                entityCount += plan.Entities.Count;
                if (entityCount > 250000) throw new ProfileValidationException("Группа профилей превышает 250000 объектов; постройте профили по отдельности.");
                result.Add(new ProfileDrawingPlan
                {
                    ProfileId = profile.ProfileId, ProfileName = profile.ProfileName, Plan = plan,
                    Settings = settings, OffsetY = offset
                });
                previousBottom = minimum + offset;
            }
            return result;
        }

        public static IReadOnlyList<SectionDrawingPlan> Sections(ProfileProject project, string selectedSectionId, bool allSections)
        {
            if (project == null || project.Kind != ProfileKind.CrossSection)
                throw new ProfileValidationException("Группы поперечников доступны только для поперечного профиля.");
            var result = new List<SectionDrawingPlan>();
            ProfileEngine.BuildInternal(SelectedProject(project, selectedSectionId, allSections), result);
            return result;
        }

        private static ProfileProject SelectedProject(ProfileProject project, string selectedSectionId, bool allSections)
        {
            if (project == null) throw new ProfileValidationException("Проект отсутствует.");
            if (project.Kind != ProfileKind.CrossSection || allSections) return project;
            // Structural validation and cloning retain every raw value; semantic
            // completeness is checked only for the section actually being drawn.
            var copy = ProjectFile.Clone(project);
            var selected = copy.Sections.Where(section => string.Equals(section.SectionId, selectedSectionId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (string.IsNullOrWhiteSpace(selectedSectionId) || selected.Count != 1)
                throw new ProfileValidationException("Выберите один поперечник для построения.");
            copy.Sections = selected;
            return copy;
        }
    }
}
