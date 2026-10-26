using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class CrossPresentationTests
{
    [Fact]
    public void TrapezoidKeepsSurveyNumbersAndDistancesSeparateFromProjectMarks()
    {
        var project = Cross(new() { Offset = -8, Existing = 10 }, new() { Offset = 0, Existing = 10 }, new() { Offset = 8, Existing = 10 });
        project.Settings.HorizontalScale = project.Settings.VerticalScale = 100;
        var section = project.Sections[0];
        section.DesignMode = SectionDesignMode.Trapezoid;
        section.ProjectBottom = 8;
        section.BottomWidth = 2;
        section.LeftSlope = 1;
        section.RightSlope = 2;
        var plan = ProfileEngine.Build(project);
        Assert.Equal(new[] { 0.0, 8, 16 }, Curves(plan, "PROF_ORD").Select(line => line.Points[0].X));
        var texts = plan.Entities.OfType<DrawText>().ToList();
        var numbers = Assert.Single(texts, text => text.Text == "№ ТОЧКИ");
        Assert.Equal(Enumerable.Range(1, 3).Select(number => number.ToString()), texts.Where(text => text.Position.X >= 0 && text.Position.Y == numbers.Position.Y).Select(text => text.Text));
        var distances = Assert.Single(texts, text => text.Text == "РАССТОЯНИЕ, м");
        Assert.Equal(new[] { "8.00", "8.00" }, texts.Where(text => text.Position.X >= 0 && text.Position.Y == distances.Position.Y).Select(text => text.Text));
        var design = Assert.Single(texts, text => text.Text == "ПРОЕКТНЫЕ\nОТМЕТКИ, м");
        Assert.Equal(new[] { "10.00", "8.00", "8.00", "10.00" }, texts.Where(text => text.Position.X >= 0 && text.Position.Y == design.Position.Y).Select(text => text.Text));
        Assert.Equal(new[] { -8.0, 0, 8 }, section.Points.Select(point => point.Offset));
        Assert.All(section.Points, point => Assert.Null(point.Design));
        Assert.Contains(texts, text => text.Text == "1:1");
        Assert.Contains(texts, text => text.Text == "1:2");
    }

    [Fact]
    public void ManualSlopeLabelsUsePhysicalRatioAndCoalesceInterpolatedCollinearSegments()
    {
        var project = Cross(new() { Offset = -4, Existing = 14, Design = 10 }, new() { Offset = -2, Existing = 14, Design = 9 },
            new() { Offset = 0, Existing = 14, Design = 8 }, new() { Offset = 2, Existing = 14, Design = 8 }, new() { Offset = 4, Existing = 14, Design = 9 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        var labels = ProfileEngine.Build(project).Entities.OfType<DrawText>().Where(text => text.Text.StartsWith("1:")).ToList();
        Assert.Equal(2, labels.Count);
        Assert.All(labels, label => Assert.Equal("1:2", label.Text));
    }

    [Fact]
    public void WaterLabelIsAboveWaterWithoutLeaderAndAreaLeadersPointInsideTheirOwnRegions()
    {
        var project = Cross(new() { Offset = -2, Existing = 10, Design = 8 }, new() { Offset = 0, Existing = 10, Design = 10 }, new() { Offset = 2, Existing = 10, Design = 12 });
        var section = project.Sections[0];
        section.DesignMode = SectionDesignMode.Points;
        section.Water = 11;
        section.SiltThickness = .4;
        section.SiltLeft = -2;
        section.SiltRight = 2;
        var plan = ProfileEngine.Build(project);
        var water = Assert.Single(Curves(plan, "PROF_WATER"));
        var waterLabel = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "УР.ВОД.11.00");
        Assert.True(waterLabel.Position.Y > water.Points[0].Y);
        Assert.DoesNotContain(Curves(plan, "PROF_LEADER"), line => line.ColorIndex == 5);
        foreach (short color in new short[] { 1, 3, 30 })
        {
            var regions = plan.Entities.OfType<DrawHatch>().Where(hatch => hatch.ColorIndex == color).ToList();
            Assert.Contains(Curves(plan, "PROF_LEADER"), leader => leader.ColorIndex == color && regions.Any(region => Inside(region, leader.Points[0])));
        }
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
    }

    [Fact]
    public void ThinPositiveHatchRegionsGetMultiplePatternLinesRegardlessOfOriginPhase()
    {
        var project = Cross(new() { Offset = 0, Existing = 10, Design = 9.999 }, new() { Offset = .001, Existing = 10, Design = 9.999 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        var hatch = Assert.Single(ProfileEngine.Build(project).Entities.OfType<DrawHatch>());
        double angle = hatch.PatternAngleDegrees * Math.PI / 180;
        var across = hatch.Boundary.Select(point => -point.X * Math.Sin(angle) + point.Y * Math.Cos(angle)).ToList();
        Assert.True(hatch.PatternSpacing > 0);
        Assert.True((across.Max() - across.Min()) / hatch.PatternSpacing >= 3 - 1e-7);
    }

    [Fact]
    public void NearbyProjectVerticesDoNotSplitMeasuredIntervalsOrShrinkTheirNumbers()
    {
        var project = SampleProjects.CrossSections();
        project.Sections = project.Sections.Take(1).ToList();
        var withProject = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        project.Sections[0].DesignMode = SectionDesignMode.None;
        var withoutProject = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        foreach (string rowName in new[] { "№ ТОЧКИ", "РАССТОЯНИЕ, м" })
        {
            var row = withProject.Single(t => t.Text == rowName);
            var actual = withProject.Where(t => t.Position.X >= 0 && t.Position.Y == row.Position.Y).ToList();
            var expected = withoutProject.Where(t => t.Position.X >= 0 && t.Position.Y == row.Position.Y).ToList();
            Assert.NotEmpty(actual);
            Assert.Equal(expected.Select(t => (t.Text, t.Position, t.Height)), actual.Select(t => (t.Text, t.Position, t.Height)));
        }
    }

    [Fact]
    public void DistanceLabelsStayCenteredAndOnlyTheirOwnShortIntervalReducesFont()
    {
        var project = Cross(new() { Offset = 0, Existing = 10 }, new() { Offset = 3, Existing = 10 },
            new() { Offset = 3.5, Existing = 10 }, new() { Offset = 7.5, Existing = 10 });
        project.Settings.CrossHorizontalScale = project.Settings.CrossVerticalScale = 100;
        project.Settings.TextHeightMm = 10;
        project.Sections[0].DesignMode = SectionDesignMode.None;
        var texts = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        var row = Assert.Single(texts, text => text.Text == "РАССТОЯНИЕ, м");
        var values = texts.Where(text => text.Position.X >= 0 && Math.Abs(text.Position.Y - row.Position.Y) < 1e-8).ToList();
        Assert.Equal(new[] { "3.00", "0.50", "4.00" }, values.Select(text => text.Text));
        Assert.Equal(.4 / 1.3, values[0].Height, 8);
        Assert.Equal(.4 / 1.3, values[2].Height, 8);
        Assert.InRange(values[1].Height, .08, .249999);
        Assert.All(values, text => Assert.Equal(-.85, text.Position.Y, 8));
    }

    [Fact]
    public void AllAreasKeepEditableLeaderLandingsWithoutArrowheads()
    {
        var project = Cross(new() { Offset = -2, Existing = 10, Design = 8 }, new() { Offset = 2, Existing = 10, Design = 12 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        project.Sections[0].SiltThickness = .4;
        project.Sections[0].SiltLeft = -2;
        project.Sections[0].SiltRight = 2;
        var plan = ProfileEngine.Build(project);
        Assert.Empty(Curves(plan, "PROF_LEADER_ARROW"));
        foreach (short color in new short[] { 1, 3, 30 })
        {
            var leader = Assert.Single(Curves(plan, "PROF_LEADER"), line => line.ColorIndex == color);
            Assert.True(leader.Points.Count >= 3);
            Assert.Equal(leader.Points[leader.Points.Count - 2].Y, leader.Points.Last().Y, 8);
            Assert.NotEqual(leader.Points[leader.Points.Count - 2].X, leader.Points.Last().X);
        }
    }

    [Fact]
    public void LargeSlopeCaptionsKeepTheirFullRotatedBoundsAboveTheCrossTable()
    {
        var project = Cross(new() { Offset = 0, Existing = 160.2 }, new() { Offset = 8.5, Existing = 160.2 },
            new() { Offset = 17, Existing = 160.2 });
        project.Settings.CrossHorizontalScale = project.Settings.CrossVerticalScale = 100;
        project.Settings.Datum = 158;
        project.Settings.TextHeightMm = 10;
        var section = project.Sections[0];
        section.DesignMode = SectionDesignMode.Trapezoid;
        section.ProjectBottom = 159.1; section.BottomWidth = 1; section.CenterOffset = 8.5;
        section.LeftSlope = section.RightSlope = 1.5; section.Water = 160.7;
        var labels = ProfileEngine.Build(project).Entities.OfType<DrawText>().Where(text => text.Text == "1:1.5").ToList();
        Assert.Equal(2, labels.Count);
        Assert.All(labels, label =>
        {
            double angle = label.RotationDegrees * Math.PI / 180;
            double width = label.Text.Length * label.Height * .75 * label.WidthFactor;
            double halfHeight = (Math.Abs(Math.Sin(angle)) * width + Math.Abs(Math.Cos(angle)) * label.Height * 1.3) / 2;
            Assert.True(label.Position.Y - halfHeight > 0);
        });
    }

    private static bool Inside(DrawHatch region, Point2 point)
    {
        bool inside = false;
        for (int index = 0, previous = region.Boundary.Count - 1; index < region.Boundary.Count; previous = index++)
        {
            var a = region.Boundary[index];
            var b = region.Boundary[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
