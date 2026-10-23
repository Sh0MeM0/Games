using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class BuildScopeTests
{
    [Fact]
    public void SelectedSectionDrawsDespiteIncompleteOtherSectionAndPreservesEntireDraft()
    {
        var project = CrossProject();
        project.Sections.Add(new CrossSection { Name = "Незавершённый", Station = 150 });
        string before = ProjectFile.Serialize(project);
        var selected = ProfileBuildScope.Build(project, project.Sections[0].SectionId, false);
        var group = Assert.Single(ProfileBuildScope.Groups(project, project.Sections[0].SectionId, false));

        Assert.Equal(project.ProfileId, group.ProfileId);
        Assert.Equal(project.Sections[0].SectionId, group.SectionId);
        Assert.Equal(0, group.OffsetY);
        Assert.Equal(selected.Entities.Count, group.Plan.Entities.Count);
        Assert.Throws<ProfileValidationException>(() => ProfileBuildScope.Build(project, null, true));
        Assert.Equal(before, ProjectFile.Serialize(project));
        group.Plan.Entities.Clear();
        Assert.NotEmpty(ProfileBuildScope.Build(project, project.Sections[0].SectionId, false).Entities);
    }

    [Fact]
    public void UnknownSelectionNeverFallsBackToFirstOrAllSections()
    {
        var project = CrossProject();
        Assert.Throws<ProfileValidationException>(() => ProfileBuildScope.Build(project, null, false));
        Assert.Throws<ProfileValidationException>(() => ProfileBuildScope.Groups(project, Guid.NewGuid().ToString("N"), false));
    }

    [Fact]
    public void SameStationSectionsStayIndependentAndGroupedLayoutMatchesCombinedDrawing()
    {
        var project = CrossProject();
        var second = ProfileInterpolation.CloneSection(project.Sections[0]);
        second.SectionId = Guid.NewGuid().ToString("N");
        second.Name = "Другое сечение на том же пикете";
        second.Points[1].Existing = 12;
        project.Sections.Add(second);
        string before = ProjectFile.Serialize(project);

        var groups = ProfileBuildScope.Groups(project, null, true);
        var combined = ProfileBuildScope.Build(project, null, true);
        Assert.Equal(2, groups.Count);
        Assert.Equal(0, groups[0].OffsetY);
        Assert.True(groups[1].OffsetY < 0);
        Assert.NotEqual(groups[0].SectionId, groups[1].SectionId);
        Assert.Equal(combined.Entities.Count, groups.Sum(group => group.Plan.Entities.Count));
        int entityIndex = 0;
        foreach (var group in groups)
            foreach (var entity in group.Plan.Entities)
            {
                var actual = combined.Entities[entityIndex++];
                Assert.Equal(entity.GetType(), actual.GetType());
                var local = Coordinates(entity).ToList();
                var placed = Coordinates(actual).ToList();
                Assert.Equal(local.Count, placed.Count);
                for (int index = 0; index < local.Count; index++)
                {
                    Assert.Equal(local[index].X, placed[index].X, 9);
                    Assert.Equal(local[index].Y + group.OffsetY, placed[index].Y, 9);
                }
            }
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Fact]
    public void SelectedLongitudinalProfileIgnoresIncompleteInactiveGeometry()
    {
        var project = LongProject();
        project.AdditionalProfiles.Add(new ProfileData { Name = "Будущий профиль" });
        string before = ProjectFile.Serialize(project);
        var selected = Assert.Single(ProfileBuildScope.Groups(project, null, false));

        Assert.Equal(project.ProfileId, selected.ProfileId);
        Assert.Null(selected.SectionId);
        Assert.NotEmpty(selected.Plan.Entities);
        Assert.Throws<ProfileValidationException>(() => ProfileBuildScope.Build(project, null, false, true));
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Fact]
    public void AllLongitudinalProfilesUseTheirOwnSettingsAndStableTabOrder()
    {
        var project = LongProject();
        var second = new ProfileData
        {
            Name = "Второй", Settings = new ProfileSettings { Datum = 0, HorizontalScale = 500, VerticalScale = 100 },
            Longitudinal = new List<LongitudinalPoint> { new() { Station = 0, Ground = 20 }, new() { Station = 10, Ground = 22 } }
        };
        project.AdditionalProfiles.Add(second);
        var activeSecond = ProfileCollection.Select(project, second.ProfileId);
        string before = ProjectFile.Serialize(activeSecond);
        var groups = ProfileBuildScope.Groups(activeSecond, null, false, true);

        Assert.Equal(new[] { project.ProfileId, second.ProfileId }, groups.Select(group => group.ProfileId));
        Assert.Equal(new[] { 1000.0, 500 }, groups.Select(group => group.Settings.HorizontalScale));
        Assert.All(groups, group => Assert.Null(group.SectionId));
        var firstGround = Assert.Single(groups[0].Plan.Entities.OfType<DrawPolyline>(), entity => entity.Layer == "PROF_GROUND");
        var secondGround = Assert.Single(groups[1].Plan.Entities.OfType<DrawPolyline>(), entity => entity.Layer == "PROF_GROUND");
        Assert.Equal(20, firstGround.Points[^1].Y - firstGround.Points[0].Y, 9);
        Assert.Equal(10, secondGround.Points[^1].Y - secondGround.Points[0].Y, 9);
        Assert.True(groups[1].OffsetY < 0);
        Assert.Equal(groups.Sum(group => group.Plan.Entities.Count), ProfileBuildScope.Build(activeSecond, null, false, true).Entities.Count);
        Assert.Equal(before, ProjectFile.Serialize(activeSecond));
    }

    [Fact]
    public void AllCrossSectionsNeverDrawInactiveProfilesEvenIfAllProfilesFlagIsSet()
    {
        var project = CrossProject();
        project.AdditionalProfiles.Add(new ProfileData { Name = "Другой незавершённый профиль" });
        var group = Assert.Single(ProfileBuildScope.Groups(project, null, true, true));
        Assert.Equal(project.ProfileId, group.ProfileId);
        Assert.Equal(project.Sections[0].SectionId, group.SectionId);
    }

    private static ProfileProject CrossProject() => new()
    {
        Kind = ProfileKind.CrossSection,
        Settings = new ProfileSettings { Datum = 0, CrossHorizontalScale = 100, CrossVerticalScale = 100 },
        Sections = new List<CrossSection>
        {
            new()
            {
                Station = 120, DesignMode = SectionDesignMode.Trapezoid, ProjectBottom = 8, BottomWidth = 2,
                CenterOffset = 5, LeftSlope = 1, RightSlope = 1,
                Points = new List<SectionPoint> { new() { Offset = 0, Existing = 10 }, new() { Offset = 10, Existing = 11 } }
            }
        }
    };

    private static ProfileProject LongProject() => new()
    {
        Kind = ProfileKind.Longitudinal,
        Settings = new ProfileSettings { Datum = 0, HorizontalScale = 1000, VerticalScale = 100 },
        Longitudinal = new List<LongitudinalPoint> { new() { Station = 0, Ground = 10 }, new() { Station = 10, Ground = 12 } }
    };

    private static IEnumerable<Point2> Coordinates(DrawEntity entity) => entity switch
    {
        DrawPolyline line => line.Points,
        DrawHatch hatch => hatch.Boundary,
        DrawText text => new[] { text.Position },
        DrawCircle circle => new[] { circle.Center },
        DrawDimension dimension => new[] { dimension.FirstPoint, dimension.SecondPoint, dimension.DimensionLinePoint },
        _ => throw new InvalidOperationException("Unexpected entity type")
    };
}
