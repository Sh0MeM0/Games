using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class SectionSurveyLayoutTests
{
    [Fact]
    public void ProjectGuidesAreRedDashedAndStopBeforeSurveyTable()
    {
        var project = Cross(new() { Offset = -8, Existing = 10 }, new() { Offset = 0, Existing = 10 }, new() { Offset = 8, Existing = 10 });
        project.Settings.HorizontalScale = project.Settings.VerticalScale = 100;
        var source = project.Sections[0];
        source.DesignMode = SectionDesignMode.Trapezoid;
        source.ProjectBottom = 8;
        source.BottomWidth = 2;
        source.LeftSlope = 1;
        source.RightSlope = 2;
        var before = ProjectFile.Serialize(project);
        var plan = ProfileEngine.Build(project);
        var guides = Curves(plan, "PROF_PROJECT_ORD");
        Assert.Equal(new[] { 5.0, 7, 9, 13 }, guides.Select(line => line.Points[0].X));
        Assert.Equal(new[] { 10.0, 8, 8, 10 }, guides.Select(line => line.Points[1].Y));
        Assert.All(guides, line =>
        {
            Assert.True(line.Dashed);
            Assert.Equal(1, line.ColorIndex);
            Assert.Equal(0, line.Points[0].Y);
            Assert.Equal(line.Points[0].X, line.Points[1].X);
        });
        Assert.All(Curves(plan, "PROF_ORD"), line =>
        {
            Assert.False(line.Dashed);
            Assert.Equal(7, line.ColorIndex);
            Assert.True(line.Points[0].Y < 0);
            Assert.Equal(10, line.Points[1].Y);
        });
        Assert.Equal(new[] { "1", "2", "3" }, Row(plan, "№ ТОЧКИ").Select(t => t.Text));
        Assert.Equal(new[] { "8.00", "8.00" }, Row(plan, "РАССТОЯНИЕ, м").Select(t => t.Text));
        Assert.False(Assert.Single(Curves(plan, "PROF_PROJECT")).Dashed);
        Assert.Equal(2, Assert.Single(plan.Entities.OfType<DrawDimension>()).SecondPoint.X -
            Assert.Single(plan.Entities.OfType<DrawDimension>()).FirstPoint.X, 9);
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Fact]
    public void ManualProjectOnlyPointDoesNotReceiveSurveyNumberOrSplitDistance()
    {
        var project = Cross(new() { DistanceFromPrevious = 0, Existing = 10 },
            new() { DistanceFromPrevious = 2, Design = 9 }, new() { DistanceFromPrevious = 3, Existing = 10 },
            new() { DistanceFromPrevious = 4, Existing = 10, Design = 11 });
        var source = project.Sections[0];
        source.UsePointDistances = true;
        source.DesignMode = SectionDesignMode.Points;
        var plan = ProfileEngine.Build(project);
        Assert.Equal(new[] { 0.0, 5, 9 }, Row(plan, "№ ТОЧКИ").Select(t => t.Position.X));
        Assert.Equal(new[] { "1", "2", "3" }, Row(plan, "№ ТОЧКИ").Select(t => t.Text));
        Assert.Equal(new[] { "5.00", "4.00" }, Row(plan, "РАССТОЯНИЕ, м").Select(t => t.Text));
        Assert.Equal(new[] { 2.5, 7 }, Row(plan, "РАССТОЯНИЕ, м").Select(t => t.Position.X));
        Assert.Equal(new[] { 0.0, 5, 9 }, Curves(plan, "PROF_ORD").Select(line => line.Points[0].X));
        Assert.Equal(new[] { 2.0, 9 }, Curves(plan, "PROF_PROJECT_ORD").Select(line => line.Points[0].X));
        Assert.Null(source.Points[1].Existing);
        Assert.Null(source.Points[2].Design);
    }

    [Fact]
    public void ProjectWithoutSurfaceHasMarksAndGuidesButNoNumbersOrIntervals()
    {
        var project = Cross(new() { Offset = 0, Design = 8 }, new() { Offset = 10, Design = 9 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        var plan = ProfileEngine.Build(project);
        Assert.Empty(Row(plan, "№ ТОЧКИ"));
        Assert.Empty(Row(plan, "РАССТОЯНИЕ, м"));
        Assert.Empty(Curves(plan, "PROF_ORD"));
        Assert.Equal(2, Curves(plan, "PROF_PROJECT_ORD").Count);
        Assert.Equal(2, plan.Entities.OfType<DrawText>().Count(t => t.ColorIndex == 1 && t.Position.Y < 0));
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(10, 0)]
    [InlineData(12, 2)]
    public void SharedOffsetUsesOneSurveyOrdinateAndOnlyExtendsToHigherProject(double design, int guideCount)
    {
        var project = Cross(new() { Offset = 0, Existing = 10, Design = design }, new() { Offset = 10, Existing = 10, Design = design });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        project.Settings.HorizontalScale = project.Settings.VerticalScale = 100;
        var plan = ProfileEngine.Build(project);
        Assert.Equal(2, Curves(plan, "PROF_ORD").Count);
        Assert.Equal(guideCount, Curves(plan, "PROF_PROJECT_ORD").Count);
        Assert.All(Curves(plan, "PROF_ORD"), line => Assert.Equal(10, line.Points.Last().Y));
        Assert.All(Curves(plan, "PROF_PROJECT_ORD"), line =>
        {
            Assert.Equal(10, line.Points.First().Y);
            Assert.Equal(design, line.Points.Last().Y);
            Assert.True(line.Dashed);
        });
    }

    [Fact]
    public void BankLabelsUseAssignedInteriorPointsAndFollowRoleChanges()
    {
        var project = Banks();
        var before = ProjectFile.Serialize(project);
        var plan = ProfileEngine.Build(project);
        CheckBank(plan, "Л.Б.", 2, 12);
        CheckBank(plan, "П.Б.", 8, 11);
        Assert.Equal(before, ProjectFile.Serialize(project));
        var points = project.Sections[0].Points;
        points[1].Role = SectionPointRole.Regular;
        points[0].Role = SectionPointRole.LeftBank;
        CheckBank(ProfileEngine.Build(project), "Л.Б.", 0, 10);
    }

    [Fact]
    public void CloseBankCaptionsMoveOutwardWithoutChangingBankPoints()
    {
        var project = Banks();
        project.Sections[0].Points[3].Offset = 2.01;
        project.Sections[0].Points[2].Offset = 2.005;
        var plan = ProfileEngine.Build(project);
        var left = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "Л.Б.");
        var right = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "П.Б.");
        Assert.True(left.Position.X < 2);
        Assert.True(right.Position.X > 2.01);
        Assert.Equal(new[] { 2.0, 2.01 }, Curves(plan, "PROF_BANK_MARKER").Select(line => line.Points[0].X));
        Assert.True(left.Position.X + left.Text.Length * left.Height * .75 * left.WidthFactor / 2 <
            right.Position.X - right.Text.Length * right.Height * .75 * right.WidthFactor / 2);
    }

    [Fact]
    public void MissingBankElevationDoesNotInventMarkerAtProjectHeight()
    {
        var project = Banks();
        project.Sections[0].Points[1].Existing = null;
        project.Sections[0].Points[1].Design = 9;
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        var plan = ProfileEngine.Build(project);
        Assert.DoesNotContain(plan.Entities.OfType<DrawText>(), t => t.Text == "Л.Б.");
        Assert.Single(Curves(plan, "PROF_BANK_MARKER"));
        Assert.Contains(plan.Entities.OfType<DrawText>(), t => t.Text == "П.Б.");
    }

    private static ProfileProject Banks()
    {
        var project = Cross(new() { Offset = 0, Existing = 10 }, new() { Offset = 2, Existing = 12, Role = SectionPointRole.LeftBank },
            new() { Offset = 5, Existing = 8 }, new() { Offset = 8, Existing = 11, Role = SectionPointRole.RightBank }, new() { Offset = 10, Existing = 10 });
        project.Settings.HorizontalScale = project.Settings.VerticalScale = 100;
        project.Sections[0].DesignMode = SectionDesignMode.None;
        return project;
    }

    private static void CheckBank(DrawingPlan plan, string label, double x, double elevation)
    {
        var text = Assert.Single(plan.Entities.OfType<DrawText>(), t => t.Text == label);
        var marker = Assert.Single(Curves(plan, "PROF_BANK_MARKER"), line => line.Points[0].X == x);
        Assert.Equal(elevation, marker.Points[0].Y);
        Assert.Equal(x, marker.Points[1].X);
        Assert.Equal(x, text.Position.X);
        Assert.True(marker.Points[1].Y > elevation);
        Assert.True(text.Position.Y - .65 * text.Height > marker.Points[1].Y);
        Assert.Equal(7, marker.ColorIndex);
        Assert.Equal(7, text.ColorIndex);
        Assert.False(marker.Dashed);
    }

    private static DrawText[] Row(DrawingPlan plan, string caption)
    {
        var row = plan.Entities.OfType<DrawText>().Single(t => t.Text == caption);
        return plan.Entities.OfType<DrawText>().Where(t => t.Position.X >= 0 && Math.Abs(t.Position.Y - row.Position.Y) < 1e-9).ToArray();
    }
}
