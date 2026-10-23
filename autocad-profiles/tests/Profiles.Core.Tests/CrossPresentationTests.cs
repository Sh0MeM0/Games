using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class CrossPresentationTests
{
    [Fact]
    public void TrapezoidDisplayAddsDaylightAndKneesWithoutChangingSavedSurveyPoints()
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
        Assert.Equal(new[] { 0.0, 5, 7, 8, 9, 13, 16 }, Curves(plan, "PROF_ORD").Select(line => line.Points[0].X));
        var texts = plan.Entities.OfType<DrawText>().ToList();
        var numbers = Assert.Single(texts, text => text.Text == "№ ТОЧКИ");
        Assert.Equal(Enumerable.Range(1, 7).Select(number => number.ToString()), texts.Where(text => text.Position.X >= 0 && text.Position.Y == numbers.Position.Y).Select(text => text.Text));
        var distances = Assert.Single(texts, text => text.Text == "РАССТОЯНИЕ, м");
        Assert.Equal(new[] { "5.00", "2.00", "1.00", "1.00", "4.00", "3.00" }, texts.Where(text => text.Position.X >= 0 && text.Position.Y == distances.Position.Y).Select(text => text.Text));
        var design = Assert.Single(texts, text => text.Text == "ПРОЕКТНЫЕ\nОТМЕТКИ, м");
        Assert.Equal(new[] { "10.00", "8.00", "8.00", "8.00", "10.00" }, texts.Where(text => text.Position.X >= 0 && text.Position.Y == design.Position.Y).Select(text => text.Text));
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
    public void NearbySupplementalColumnsKeepExactOffsetsAndStaggerTheirLabels()
    {
        var project = SampleProjects.CrossSections();
        project.Sections = project.Sections.Take(1).ToList();
        var plan = ProfileEngine.Build(project);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        var ninth = Assert.Single(texts, text => text.Text == "9");
        var tenth = Assert.Single(texts, text => text.Text == "10");
        // The right bank rises 0.05 m over 4 m; intersect that slope with x = 0.3 + 1.5 * (z - 141.2).
        Assert.Equal(4.125 / .98125 - 4, tenth.Position.X - ninth.Position.X, 8);
        Assert.NotEqual(ninth.Position.Y, tenth.Position.Y);
        var shortDistances = texts.Where(text => text.Text == "0.30").ToList();
        Assert.Equal(2, shortDistances.Count);
        Assert.NotEqual(shortDistances[0].Position.Y, shortDistances[1].Position.Y);
        Assert.Contains(plan.Warnings, warning => warning.Contains("разнесены по высоте"));
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
