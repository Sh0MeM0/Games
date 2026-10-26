using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class SectionElevationOwnershipTests
{
    [Fact]
    public void TrapezoidLabelsOnlySurveyPointsAndActualProjectVertices()
    {
        var project = Trapezoid();
        var source = project.Sections[0];
        var before = ProjectFile.Serialize(project);
        var earthworks = Earthworks.Calculate(source);
        var table = SectionDisplayGeometry.BuildTable(earthworks, source);
        Assert.Equal(new[] { -8.0, 0, 8 }, table.Where(p => p.Existing.HasValue).Select(p => p.Offset));
        Assert.Equal(new[] { -3.0, -1, 1, 5 }, table.Where(p => p.Design.HasValue).Select(p => p.Offset));
        Assert.Equal(SectionDisplayGeometry.Build(earthworks).Select(p => p.Offset), table.Select(p => p.Offset));

        var plan = ProfileEngine.Build(project);
        Assert.Equal(new[] { 0.0, 8, 16 }, Labels(plan, 7).Select(t => t.Position.X));
        Assert.Equal(new[] { 5.0, 7, 9, 13 }, Labels(plan, 1).Select(t => t.Position.X));
        Assert.Equal(new[] { "10.00", "8.00", "8.00", "10.00" }, Labels(plan, 1).Select(t => t.Text));
        Assert.Equal(3, Assert.Single(Curves(plan, "PROF_EXISTING")).Points.Count);
        Assert.Equal(4, Assert.Single(Curves(plan, "PROF_PROJECT")).Points.Count);
        Assert.Equal(10, earthworks.CutArea.Value, 8);
        Assert.Equal(0, earthworks.FillArea.Value, 8);
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Fact]
    public void ManualDesignKeepsInterpolationButLabelsOnlyEnteredAnchors()
    {
        var project = Cross(new() { Existing = 12, Design = 9, DistanceFromPrevious = 0 },
            new() { Existing = 13, DistanceFromPrevious = 4 },
            new() { Existing = 12, Design = 11, DistanceFromPrevious = 6 });
        var source = project.Sections[0];
        source.DesignMode = SectionDesignMode.Points;
        source.UsePointDistances = true;
        var result = Earthworks.Calculate(source);
        Assert.Equal(9.8, result.ResolvedSection.Points[1].Design.Value, 9);
        var table = SectionDisplayGeometry.BuildTable(result, source);
        Assert.Null(table.Single(p => p.Offset == 4).Design);
        Assert.Equal(new[] { 0.0, 10 }, table.Where(p => p.Design.HasValue).Select(p => p.Offset));
        var plan = ProfileEngine.Build(project);
        Assert.Equal(new[] { "9.00", "11.00" }, Labels(plan, 1).Select(t => t.Text));
        Assert.Equal(3, Labels(plan, 7).Length);
        Assert.Equal(3, Assert.Single(Curves(plan, "PROF_PROJECT")).Points.Count);
        Assert.Null(source.Points[1].Design);
        Assert.All(source.Points, p => Assert.Equal(0, p.Offset));
    }

    [Fact]
    public void CrossingInsideSegmentsDoesNotAddEitherElevationLabel()
    {
        var project = Cross(new() { Offset = 0, Existing = 10, Design = 8 },
            new() { Offset = 10, Existing = 8, Design = 10 });
        var source = project.Sections[0];
        source.DesignMode = SectionDesignMode.Points;
        var result = Earthworks.Calculate(source);
        var intersection = SectionDisplayGeometry.BuildTable(result, source).Single(p => p.Offset == 5);
        Assert.Null(intersection.Existing);
        Assert.Null(intersection.Design);
        var plan = ProfileEngine.Build(project);
        Assert.Equal(2, Curves(plan, "PROF_ORD").Count);
        Assert.Equal(2, Labels(plan, 7).Length);
        Assert.Equal(2, Labels(plan, 1).Length);
        Assert.Equal(5, result.CutArea.Value, 8);
        Assert.Equal(5, result.FillArea.Value, 8);
    }

    [Fact]
    public void SharedVertexRetainsBothMarksAndFloatingDaylightSnapsToSurvey()
    {
        var project = Trapezoid();
        var source = project.Sections[0];
        source.Points = new() { new() { Offset = .1, Existing = 10.1 },
            new() { Offset = 1.5, Existing = 10.1 }, new() { Offset = 10.1, Existing = 10.1 } };
        source.CenterOffset = .8;
        source.ProjectBottom = 9.9;
        source.BottomWidth = 1;
        source.LeftSlope = source.RightSlope = 1;
        var table = SectionDisplayGeometry.BuildTable(Earthworks.Calculate(source), source);
        Assert.Equal(5, table.Count);
        foreach (double offset in new[] { .1, 1.5 })
        {
            var shared = table.Single(p => p.Offset == offset);
            Assert.Equal(10.1, shared.Existing.Value, 9);
            Assert.Equal(10.1, shared.Design.Value, 9);
        }
        Assert.Equal(3, table.Count(p => p.Existing.HasValue));
        Assert.Equal(4, table.Count(p => p.Design.HasValue));
    }

    [Fact]
    public void LinkedProjectAnchorGetsItsLiveMarkWithoutLabelingInterpolatedGaps()
    {
        var project = Cross(new() { Offset = 0, Existing = 12, Design = 10 },
            new() { Offset = 2, Existing = 12 }, new() { Offset = 4, Existing = 12 },
            new() { Offset = 6, Existing = 12 }, new() { Offset = 8, Existing = 12, Design = 10 });
        var source = project.Sections[0];
        source.DesignMode = SectionDesignMode.Points;
        source.UseLongitudinalProjectBottom = true;
        source.ProjectAnchorIndex = 2;
        project.Longitudinal.Add(new() { Station = source.Station.Value, Ground = 12, ProjectBottom = 8 });
        Assert.Equal(new[] { "10.00", "8.00", "10.00" }, Labels(ProfileEngine.Build(project), 1).Select(t => t.Text));
        project.Longitudinal[0].ProjectBottom = 7;
        Assert.Equal(new[] { "10.00", "7.00", "10.00" }, Labels(ProfileEngine.Build(project), 1).Select(t => t.Text));
        Assert.All(source.Points.Skip(1).Take(3), p => Assert.Null(p.Design));
    }

    [Fact]
    public void MultipleSectionsUseTheirOwnAuthoredDesignAnchors()
    {
        var project = Cross(new() { Offset = 0, Existing = 12, Design = 10 },
            new() { Offset = 4, Existing = 12 }, new() { Offset = 8, Existing = 12, Design = 10 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        var other = Cross(new() { Offset = 0, Existing = 12, Design = 9 },
            new() { Offset = 4, Existing = 12, Design = 8 }, new() { Offset = 8, Existing = 12, Design = 9 }).Sections[0];
        other.DesignMode = SectionDesignMode.Points;
        other.Station = 150;
        project.Sections.Add(other);
        Assert.Equal(new[] { "10.00", "10.00", "9.00", "8.00", "9.00" },
            Labels(ProfileEngine.Build(project), 1).Select(t => t.Text));
    }

    [Fact]
    public void GridAndOrdinatesUseBlackWhileProjectAndWaterKeepTheirColors()
    {
        foreach (var project in new[] { SampleProjects.Longitudinal(), SampleProjects.NewChannel(),
            SampleProjects.Nvk(), SampleProjects.CrossSections() })
        {
            var plan = ProfileEngine.Build(project);
            var grid = plan.Entities.OfType<DrawPolyline>().Where(e => e.Layer == "PROF_GRID" || e.Layer == "PROF_ORD").ToList();
            Assert.NotEmpty(grid);
            Assert.All(grid, e => Assert.Equal(7, e.ColorIndex));
            Assert.All(Curves(plan, "PROF_PROJECT"), e => Assert.Equal(1, e.ColorIndex));
            Assert.All(Curves(plan, "PROF_WATER"), e => Assert.Equal(5, e.ColorIndex));
        }
    }

    private static DrawText[] Labels(DrawingPlan plan, short color) => plan.Entities.OfType<DrawText>()
        .Where(t => t.Position.X >= 0 && t.Position.Y < 0 && t.RotationDegrees == 90 && t.ColorIndex == color).ToArray();

    private static ProfileProject Trapezoid()
    {
        var project = Cross(new() { Offset = -8, Existing = 10 }, new() { Offset = 0, Existing = 10 }, new() { Offset = 8, Existing = 10 });
        project.Settings.HorizontalScale = project.Settings.VerticalScale = 100;
        var section = project.Sections[0];
        section.DesignMode = SectionDesignMode.Trapezoid;
        section.ProjectBottom = 8;
        section.BottomWidth = 2;
        section.LeftSlope = 1;
        section.RightSlope = 2;
        return project;
    }
}
