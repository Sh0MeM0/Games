using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ManualSectionCompletionTests
{
    [Fact]
    public void NewMeasuredStationInheritsIndependentDimensionAnchorsByDistance()
    {
        var project = Project();
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.Station = 25;
        var result = ProjectSynchronization.CommitSection(project, 0, before, entered);
        var row = result.Longitudinal.Single(point => point.Station == 25);
        Assert.Equal(2.5, row.BottomWidth);
        Assert.Equal(1.5, row.LeftSlope);
        Assert.Equal(3.5, row.RightSlope);
        Assert.Equal(row.BottomWidth, result.Sections[0].BottomWidth);
        Assert.Equal(row.LeftSlope, result.Sections[0].LeftSlope);
        Assert.Equal(row.RightSlope, result.Sections[0].RightSlope);
        Assert.Equal(new[] { 0.0, 25, 100 }, result.Longitudinal.Select(point => point.Station));
        Assert.Equal(SectionDesignMode.None, ProfileInterpolation.EffectiveDesignMode(result.Sections[0]));
        Assert.Null(project.Sections[0].Station);
        Assert.Null(project.Sections[0].BottomWidth);
        Assert.Equal(2, project.Longitudinal.Count);
        ProfileEngine.Build(new ProfileProject { Kind = ProfileKind.CrossSection, Sections = result.Sections, Longitudinal = result.Longitudinal });
    }

    [Theory]
    [InlineData(-20, 2, 1, 4)]
    [InlineData(150, 4, 3, 2)]
    public void NewStationsOutsideKnownRangeUseNearestDimensionsWithoutElevationExtrapolation(double station, double width, double left, double right)
    {
        var project = Project();
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.Station = station;
        var result = ProjectSynchronization.CommitSection(project, 0, before, entered);
        var row = result.Longitudinal.Single(point => point.Station == station);
        Assert.Equal(width, row.BottomWidth);
        Assert.Equal(left, row.LeftSlope);
        Assert.Equal(right, row.RightSlope);
        Assert.Null(row.ProjectBottom);
    }

    [Fact]
    public void ExistingAndManuallyEnteredDimensionsWinOverSuggestions()
    {
        var project = Project();
        project.Longitudinal.Add(new LongitudinalPoint { Station = 25, BottomWidth = 7.123456789 });
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.Station = 25;
        entered.LeftSlope = 6.987654321;
        var result = ProjectSynchronization.CommitSection(project, 0, before, entered);
        var row = result.Longitudinal.Single(point => point.Station == 25);
        Assert.Equal(7.123456789, row.BottomWidth);
        Assert.Equal(6.987654321, row.LeftSlope);
        Assert.Equal(3.5, row.RightSlope);
        Assert.Equal(7.123456789, result.Sections[0].BottomWidth);
        Assert.Equal(6.987654321, result.Sections[0].LeftSlope);
    }

    [Fact]
    public void ExplicitDimensionClearRemovesAnchorAndShowsNeighbourInterpolation()
    {
        var project = Bind(Project(), 25);
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.BottomWidth = null;
        var result = ProjectSynchronization.CommitSection(project, 0, before, entered);
        Assert.Null(result.Longitudinal.Single(point => point.Station == 25).BottomWidth);
        // The raw anchor stays cleared; the linked section shows interpolation just like elevations.
        Assert.Equal(2.5, result.Sections[0].BottomWidth);
        before = ProfileInterpolation.CloneSection(result.Sections[0]);
        entered = ProfileInterpolation.CloneSection(before);
        entered.Name = "Местное имя";
        result = ProjectSynchronization.CommitSection(result, 0, before, entered);
        Assert.Null(result.Longitudinal.Single(point => point.Station == 25).BottomWidth);
        // The raw anchor stays cleared; the linked section shows interpolation just like elevations.
        Assert.Equal(2.5, result.Sections[0].BottomWidth);
    }

    [Fact]
    public void MissingDimensionColumnRemainsUnknownAndInactiveProfileIsNotSampled()
    {
        var old = Project();
        var project = ProfileCollection.Add(old, "Другой канал");
        project.Longitudinal.Add(new LongitudinalPoint { Station = 0, BottomWidth = 9 });
        project.Sections.Add(Measurement());
        var result = Bind(project, 25);
        var row = result.Longitudinal.Single(point => point.Station == 25);
        Assert.Equal(9, row.BottomWidth);
        Assert.Null(row.LeftSlope);
        Assert.Null(row.RightSlope);
        var inactive = ProfileCollection.Select(result, old.ProfileId);
        Assert.Equal(2, inactive.Longitudinal.Count);
        Assert.Null(inactive.Sections[0].Station);
    }

    [Theory]
    [InlineData(SectionDesignMode.None)]
    [InlineData(SectionDesignMode.Automatic)]
    public void FirstLongitudinalProjectElevationEnablesCapturedSectionAndKeepsLaterEditsLive(SectionDesignMode initialMode)
    {
        var project = Bind(Project(), 25);
        project.Sections[0].DesignMode = initialMode;
        project.Sections[0].ProjectAnchorIndex = null;
        var result = ProjectSynchronization.SetSharedValue(project, 25, SharedProfileField.ProjectBottom, 7.123456789);
        var section = result.Sections[0];
        Assert.Equal(SectionDesignMode.Trapezoid, ProfileInterpolation.EffectiveDesignMode(section));
        Assert.Equal(1, section.ProjectAnchorIndex);
        Assert.Equal(3, section.CenterOffset);
        Assert.Equal(7.123456789, section.ProjectBottom);
        Assert.True(section.UseLongitudinalProjectBottom);
        result = ProjectSynchronization.SetSharedValue(result, 25, SharedProfileField.ProjectBottom, 7.987654321);
        var resolved = ProfileInterpolation.ResolveSection(result.Sections[0], result.Longitudinal);
        Assert.Equal(7.987654321, resolved.ProjectBottom);
        Assert.Equal(3, resolved.CenterOffset);
        Assert.Null(project.Sections[0].ProjectBottom);
    }

    [Fact]
    public void StaleSectionBufferCannotUndoNewLongitudinalModeAndAnchor()
    {
        var project = Bind(Project(), 25);
        project.Sections[0].DesignMode = SectionDesignMode.None;
        project.Sections[0].ProjectAnchorIndex = null;
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var refreshed = ProjectSynchronization.SetSharedValue(project, 25, SharedProfileField.ProjectBottom, 7);
        var result = ProjectSynchronization.CommitSection(refreshed, 0, before, before);
        Assert.Equal(SectionDesignMode.Trapezoid, ProfileInterpolation.EffectiveDesignMode(result.Sections[0]));
        Assert.Equal(1, result.Sections[0].ProjectAnchorIndex);
        Assert.Equal(3, result.Sections[0].CenterOffset);
        Assert.Equal(7, result.Sections[0].ProjectBottom);
    }

    [Fact]
    public void DeliberatelyDisabledExistingProjectAndManualCenterArePreserved()
    {
        var project = Bind(Project(), 25);
        var section = project.Sections[0];
        section.DesignMode = SectionDesignMode.None;
        section.ProjectBottom = 7;
        section.ProjectAnchorIndex = null;
        section.CenterOffset = 4.125;
        var result = ProjectSynchronization.SetSharedValue(project, 25, SharedProfileField.ProjectBottom, 7.5);
        Assert.Equal(SectionDesignMode.None, result.Sections[0].DesignMode);
        Assert.Null(result.Sections[0].ProjectAnchorIndex);
        Assert.Equal(4.125, result.Sections[0].CenterOffset);
        Assert.Equal(7.5, result.Sections[0].ProjectBottom);
    }

    [Fact]
    public void NewLocalProjectElevationIsCommittedToLongitudinalAndChoosesCenter()
    {
        var project = Bind(Project(), 25);
        project.Sections[0].DesignMode = SectionDesignMode.None;
        project.Sections[0].ProjectAnchorIndex = null;
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.ProjectBottom = 6.123456789;
        var result = ProjectSynchronization.CommitSection(project, 0, before, entered);
        Assert.Equal(6.123456789, result.Longitudinal.Single(point => point.Station == 25).ProjectBottom);
        Assert.Equal(SectionDesignMode.Trapezoid, ProfileInterpolation.EffectiveDesignMode(result.Sections[0]));
        Assert.Equal(1, result.Sections[0].ProjectAnchorIndex);
        Assert.Equal(3, result.Sections[0].CenterOffset);
        var restored = ProjectFile.Deserialize(ProjectFile.Serialize(result));
        Assert.Equal(6.123456789, restored.Sections[0].ProjectBottom);
        Assert.Equal(1, restored.Sections[0].ProjectAnchorIndex);
    }

    [Fact]
    public void AnchorKeepsExplicitColumnAndDoesNotInventMissingDistances()
    {
        var section = Measurement();
        section.ProjectAnchorIndex = 2;
        ProfileInterpolation.EnsureProjectAnchor(section);
        Assert.Equal(2, section.ProjectAnchorIndex);
        Assert.Equal(10, section.CenterOffset);
        section.Points[1].DistanceFromPrevious = null;
        section.ProjectAnchorIndex = null;
        section.CenterOffset = 17;
        ProfileInterpolation.EnsureProjectAnchor(section);
        Assert.Equal(1, section.ProjectAnchorIndex);
        Assert.Equal(17, section.CenterOffset);
        Assert.Null(section.Points[1].DistanceFromPrevious);
    }

    [Fact]
    public void FlatSurveyBottomKeepsTrueMidpointWithoutAddingOrMovingMeasuredPoints()
    {
        var section = DrawingSurvey.BuildSection(new List<SectionSurveyPick>
        {
            new() { Position = new Point2(0, 0), Values = new SectionPoint { Existing = 12 } },
            new() { Position = new Point2(4, 0), Values = new SectionPoint { Existing = 8 } },
            new() { Position = new Point2(8, 0), Values = new SectionPoint { Existing = 8 } },
            new() { Position = new Point2(12, 0), Values = new SectionPoint { Existing = 13 } }
        }, SurveySourceUnits.ModelMetres());
        Assert.Null(section.ProjectAnchorIndex);
        Assert.Equal(6, section.CenterOffset);
        var project = Project();
        project.Sections[0] = section;
        project = Bind(project, 25);
        var result = ProjectSynchronization.SetSharedValue(project, 25, SharedProfileField.ProjectBottom, 7);
        var resolved = ProfileInterpolation.ResolveSection(result.Sections[0], result.Longitudinal);
        Assert.Equal(6, resolved.CenterOffset);
        Assert.Null(result.Sections[0].ProjectAnchorIndex);
        Assert.Equal(new[] { 0.0, 4, 8, 12 }, resolved.Points.Select(point => point.Offset));
    }

    private static ProfileProject Bind(ProfileProject project, double station)
    {
        var before = ProfileInterpolation.CloneSection(project.Sections[0]);
        var entered = ProfileInterpolation.CloneSection(before);
        entered.Station = station;
        return ProjectSynchronization.CommitSection(project, 0, before, entered);
    }

    private static ProfileProject Project() => new()
    {
        Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, BottomWidth = 2, LeftSlope = 1, RightSlope = 4 },
            new() { Station = 100, BottomWidth = 4, LeftSlope = 3, RightSlope = 2 }
        },
        Sections = new List<CrossSection> { Measurement() }
    };

    private static CrossSection Measurement() => DrawingSurvey.BuildSection(new List<SectionSurveyPick>
    {
        new() { Position = new Point2(0, 0), Values = new SectionPoint { Existing = 12 } },
        new() { Position = new Point2(3, 0), Values = new SectionPoint { Existing = 8 } },
        new() { Position = new Point2(10, 0), Values = new SectionPoint { Existing = 13 } }
    }, SurveySourceUnits.ModelMetres());
}
