using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class CrossSectionTests
{
    private static ProfileProject Trapezoid(double halfSurveyWidth = 10)
    {
        var project = Cross(new() { Offset = -halfSurveyWidth, Existing = 10 },
            new() { Offset = 0, Existing = 10 }, new() { Offset = halfSurveyWidth, Existing = 10 });
        var section = project.Sections[0];
        section.ProjectBottom = 4;
        section.BottomWidth = 4;
        section.LeftSlope = 1;
        section.RightSlope = 1;
        return project;
    }

    [Fact]
    public void TrapezoidRaysEndAtMeasuredTerrainIntersections()
    {
        var design = Assert.Single(Curves(ProfileEngine.Build(Trapezoid()), "PROF_PROJECT"));
        Assert.False(design.Closed);
        Assert.Equal(4, design.Points.Count);
        var expected = new[] { new Point2(2, 100), new Point2(8, 40), new Point2(12, 40), new Point2(18, 100) };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, design.Points[i].X, 8);
            Assert.Equal(expected[i].Y, design.Points[i].Y, 8);
        }
    }

    [Fact]
    public void SlopesRepresentHorizontalDistancePerVerticalMetre()
    {
        var project = Trapezoid(20);
        project.Sections[0].LeftSlope = 2;
        project.Sections[0].RightSlope = 0.5;
        var design = Assert.Single(Curves(ProfileEngine.Build(project), "PROF_PROJECT"));
        // Six metres of rise: left run=12m, right run=3m; bottom edges are -2 and +2.
        Assert.Equal(6, design.Points[0].X, 8);
        Assert.Equal(18, design.Points[1].X, 8);
        Assert.Equal(22, design.Points[2].X, 8);
        Assert.Equal(25, design.Points[3].X, 8);
    }

    [Fact]
    public void TrapezoidIntersectionsUseInterpolatedUnevenTerrain()
    {
        var project = Trapezoid();
        project.Sections[0].BottomWidth = 2;
        project.Sections[0].Points[0].Existing = 10;
        project.Sections[0].Points[1].Existing = 5;
        project.Sections[0].Points[2].Existing = 8;
        var design = Assert.Single(Curves(ProfileEngine.Build(project), "PROF_PROJECT"));
        // Solve left z=3-x versus terrain z=5-0.5x, and right z=3+x versus z=5+0.3x.
        Assert.Equal(6, design.Points[0].X, 8);
        Assert.Equal(70, design.Points[0].Y, 8);
        Assert.Equal(10 + 20.0 / 7, design.Points[3].X, 8);
        Assert.Equal((3 + 20.0 / 7) * 10, design.Points[3].Y, 8);
    }

    [Fact]
    public void CenterOffsetMovesTrapezoidWithoutMovingSurvey()
    {
        var project = Trapezoid(20);
        var initial = ProfileEngine.Build(project);
        project.Sections[0].CenterOffset = 3;
        var moved = ProfileEngine.Build(project);
        var oldDesign = Assert.Single(Curves(initial, "PROF_PROJECT"));
        var newDesign = Assert.Single(Curves(moved, "PROF_PROJECT"));
        for (var i = 0; i < oldDesign.Points.Count; i++)
            Assert.Equal(oldDesign.Points[i].X + 3, newDesign.Points[i].X, 8);
        Assert.Equal(Assert.Single(Curves(initial, "PROF_EXISTING")).Points.Select(p => p.X),
            Assert.Single(Curves(moved, "PROF_EXISTING")).Points.Select(p => p.X));
    }

    [Fact]
    public void SurveyThatDoesNotReachSideIntersectionsIsRejectedWithActionableMessage()
    {
        var error = Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(Trapezoid(5)));
        Assert.Matches("(?i)(продл|расшир|добав|измер|съем|съём)", error.Message);
    }

    [Fact]
    public void TerrainGapCannotSupplyAnInventedIntersection()
    {
        var project = Trapezoid();
        project.Sections[0].Points[1].Existing = null;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Theory]
    [InlineData("bottom")]
    [InlineData("width")]
    [InlineData("left")]
    [InlineData("right")]
    public void TrapezoidParametersMustBeSuppliedTogether(string missing)
    {
        var project = Trapezoid();
        var section = project.Sections[0];
        switch (missing)
        {
            case "bottom": section.ProjectBottom = null; break;
            case "width": section.BottomWidth = null; break;
            case "left": section.LeftSlope = null; break;
            case "right": section.RightSlope = null; break;
        }
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ZeroOrNegativeSideSlopeIsRejected(double slope)
    {
        var project = Trapezoid();
        project.Sections[0].LeftSlope = slope;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Fact]
    public void ExplicitDesignCannotBeMixedWithTrapezoidParameters()
    {
        var project = Trapezoid();
        project.Sections[0].Points[0].Design = 8;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Fact]
    public void DesignAndWaterAreOptional()
    {
        var project = Cross(new() { Offset = -5, Existing = 10 }, new() { Offset = 5, Existing = 12 });
        var plan = ProfileEngine.Build(project);
        Assert.NotEmpty(Curves(plan, "PROF_EXISTING"));
        Assert.Empty(Curves(plan, "PROF_PROJECT"));
        Assert.Empty(Curves(plan, "PROF_WATER"));
    }

    private static ProfileProject TwoBasins()
    {
        var project = Cross(new() { Offset = -6, Existing = 6 }, new() { Offset = -4, Existing = 2 },
            new() { Offset = -2, Existing = 6 }, new() { Offset = 0, Existing = 6 },
            new() { Offset = 2, Existing = 2 }, new() { Offset = 4, Existing = 6 });
        project.Sections[0].Water = 4;
        return project;
    }

    [Fact]
    public void WaterIsClippedSeparatelyToEachSubmergedMeasuredInterval()
    {
        var water = Curves(ProfileEngine.Build(TwoBasins()), "PROF_WATER")
            .OrderBy(line => line.Points[0].X).ToList();
        Assert.Equal(2, water.Count);
        Assert.Equal(new[] { 1.0, 3.0 }, water[0].Points.Select(p => p.X));
        Assert.Equal(new[] { 7.0, 9.0 }, water[1].Points.Select(p => p.X));
        Assert.All(water, line =>
        {
            Assert.Equal(5, line.ColorIndex);
            Assert.All(line.Points, point => Assert.Equal(40, point.Y, 8));
        });
    }

    [Fact]
    public void AddingExplicitDesignDoesNotChangeWaterExtentsBasedOnExistingGround()
    {
        var project = TwoBasins();
        var before = Curves(ProfileEngine.Build(project), "PROF_WATER");
        foreach (var point in project.Sections[0].Points)
            point.Design = 1;
        var after = Curves(ProfileEngine.Build(project), "PROF_WATER");
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Points.Select(p => p.X), after[i].Points.Select(p => p.X));
            Assert.Equal(before[i].Points.Select(p => p.Y), after[i].Points.Select(p => p.Y));
        }
    }

    [Fact]
    public void WaterCannotBridgeMissingSurveyEvenWhenDesignIsContinuous()
    {
        var project = Cross(new() { Offset = -4, Existing = 6, Design = 1 },
            new() { Offset = -2, Existing = 2, Design = 1 }, new() { Offset = 0, Design = 1 },
            new() { Offset = 2, Existing = 2, Design = 1 }, new() { Offset = 4, Existing = 6, Design = 1 });
        project.Sections[0].Water = 4;
        var water = Curves(ProfileEngine.Build(project), "PROF_WATER");
        Assert.Equal(2, water.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, water[0].Points.Select(p => p.X));
        Assert.Equal(new[] { 6.0, 7.0 }, water[1].Points.Select(p => p.X));
    }

    [Fact]
    public void WaterBelowAllGroundCreatesNoWaterCurve()
    {
        var project = TwoBasins();
        project.Sections[0].Water = 1;
        Assert.Empty(Curves(ProfileEngine.Build(project), "PROF_WATER"));
    }

    [Fact]
    public void WaterCanUseExplicitDesignWhenMeasuredGroundIsEntirelyAbsentWithWarning()
    {
        var project = Cross(new() { Offset = -2, Design = 6 }, new() { Offset = 0, Design = 2 },
            new() { Offset = 2, Design = 6 });
        project.Sections[0].Water = 4;
        var plan = ProfileEngine.Build(project);
        var water = Assert.Single(Curves(plan, "PROF_WATER"));
        Assert.Equal(new[] { 1.0, 3.0 }, water.Points.Select(p => p.X));
        Assert.Contains(plan.Warnings, warning => warning.IndexOf("проект", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public void MoreThanTwoHundredSectionsIsRejected()
    {
        var project = Trapezoid();
        var template = project.Sections[0];
        project.Sections = Enumerable.Range(0, 201).Select(i => new CrossSection
        {
            Name = "Створ " + i,
            Points = template.Points.Select(point => new SectionPoint
            {
                Offset = point.Offset,
                Existing = point.Existing
            }).ToList()
        }).ToList();
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }
}
