using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class EngineGeometryTests
{
    internal static ProfileProject Longitudinal(params double?[] heights)
    {
        var project = new ProfileProject
        {
            Kind = ProfileKind.Longitudinal,
            Settings = new ProfileSettings
            {
                Title = "Контрольный профиль",
                HorizontalScale = 1000,
                VerticalScale = 100,
                MetresPerDrawingUnit = 1,
                Datum = 0
            }
        };
        for (var i = 0; i < heights.Length; i++)
            project.Longitudinal.Add(new LongitudinalPoint { Station = 100 + i * 20, Ground = heights[i] });
        return project;
    }

    internal static ProfileProject Cross(params SectionPoint[] points)
    {
        return new ProfileProject
        {
            Kind = ProfileKind.CrossSection,
            Settings = new ProfileSettings
            {
                Title = "Контрольные поперечники",
                HorizontalScale = 1000,
                VerticalScale = 100,
                MetresPerDrawingUnit = 1,
                Datum = 0
            },
            Sections = new List<CrossSection>
            {
                new() { Name = "Створ А", Station = 125, Points = points.ToList() }
            }
        };
    }

    internal static List<DrawPolyline> Curves(DrawingPlan plan, string layer) =>
        plan.Entities.OfType<DrawPolyline>().Where(entity => entity.Layer == layer).ToList();

    [Theory]
    [InlineData(1000, 100, 1, 20, 20)]
    [InlineData(500, 100, 1, 20, 10)]
    [InlineData(1000, 200, 1, 20, 10)]
    [InlineData(1000, 100, 0.001, 20000, 20000)]
    public void StationAndElevationDistancesRespectScalesAndDrawingUnits(
        double horizontal, double vertical, double metresPerUnit, double expectedDx, double expectedDy)
    {
        var project = Longitudinal(10, 12);
        project.Settings.HorizontalScale = horizontal;
        project.Settings.VerticalScale = vertical;
        project.Settings.MetresPerDrawingUnit = metresPerUnit;
        var line = Assert.Single(Curves(ProfileEngine.Build(project), "PROF_GROUND"));
        Assert.Equal(2, line.Points.Count);
        Assert.Equal(0, line.Points[0].X, 8);
        Assert.Equal(expectedDx, line.Points[1].X - line.Points[0].X, 8);
        Assert.Equal(expectedDy, line.Points[1].Y - line.Points[0].Y, 8);
    }

    [Fact]
    public void ExplicitDatumDefinesElevationOrigin()
    {
        var project = Longitudinal(10, 12);
        project.Settings.Datum = 7;
        var plan = ProfileEngine.Build(project);
        Assert.Equal(7, plan.Datum);
        var line = Assert.Single(Curves(plan, "PROF_GROUND"));
        Assert.Equal(30, line.Points[0].Y, 8);
        Assert.Equal(50, line.Points[1].Y, 8);
    }

    [Fact]
    public void AutomaticDatumIsOneMetreBelowFloorOfLowestIncludedElevation()
    {
        var project = Longitudinal(10.2, 12.3);
        project.Settings.Datum = null;
        project.Longitudinal[0].ProjectBottom = 8.7;
        project.Longitudinal[1].ProjectBottom = 9.1;
        Assert.Equal(7, ProfileEngine.Build(project).Datum);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void ExplicitDatumMustBeStrictlyBelowIncludedElevations(double datum)
    {
        var project = Longitudinal(10, 12);
        project.Settings.Datum = datum;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleAndUnitsAreRejectedBeforeGeometryIsProduced(double value)
    {
        var horizontal = Longitudinal(10, 12);
        horizontal.Settings.HorizontalScale = value;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(horizontal));
        var vertical = Longitudinal(10, 12);
        vertical.Settings.VerticalScale = value;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(vertical));
        var units = Longitudinal(10, 12);
        units.Settings.MetresPerDrawingUnit = value;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(units));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(99)]
    public void DuplicateOrDescendingStationsFailWithoutSilentlySorting(double secondStation)
    {
        var project = Longitudinal(10, 12);
        project.Longitudinal[1].Station = secondStation;
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
        Assert.Equal(100, project.Longitudinal[0].Station);
        Assert.Equal(secondStation, project.Longitudinal[1].Station);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    public void DuplicateOrDescendingOffsetsFailWithoutSilentlySorting(double secondOffset)
    {
        var project = Cross(new() { Offset = -2, Existing = 10 }, new() { Offset = secondOffset, Existing = 12 });
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
        Assert.Equal(-2, project.Sections[0].Points[0].Offset);
        Assert.Equal(secondOffset, project.Sections[0].Points[1].Offset);
    }

    [Fact]
    public void BlankLongitudinalElevationsInterpolateByStationAndRecalculateFromAnchors()
    {
        var project = Longitudinal(10, null, null, 20);
        project.Longitudinal[1].Station = 107;
        project.Longitudinal[2].Station = 145;
        project.Longitudinal[3].Station = 200;
        project.Longitudinal[0].ProjectBottom = 8;
        project.Longitudinal[3].ProjectBottom = 10;
        var plan = ProfileEngine.Build(project);
        var ground = Assert.Single(Curves(plan, "PROF_GROUND"));
        var design = Assert.Single(Curves(plan, "PROF_PROJECT"));
        Assert.Equal(new[] { 0.0, 7.0, 45.0, 100.0 }, ground.Points.Select(p => p.X));
        Assert.Equal(107, ground.Points[1].Y, 8);
        Assert.Equal(145, ground.Points[2].Y, 8);
        Assert.Equal(81.4, design.Points[1].Y, 8);
        Assert.Equal(89, design.Points[2].Y, 8);
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "8.14" && text.ColorIndex == 1);
        Assert.Null(project.Longitudinal[1].Ground);
        Assert.Null(project.Longitudinal[2].ProjectBottom);
        project.Longitudinal[3].Ground = 30;
        var changed = Assert.Single(Curves(ProfileEngine.Build(project), "PROF_GROUND"));
        Assert.Equal(114, changed.Points[1].Y, 8);
        Assert.Equal(190, changed.Points[2].Y, 8);
        Assert.Null(project.Longitudinal[1].Ground);
    }

    [Fact]
    public void MissingLongitudinalEndElevationsAreNotExtrapolatedOrMarkedWithCircles()
    {
        var project = Longitudinal(null, 10, 12, null);
        var plan = ProfileEngine.Build(project);
        var line = Assert.Single(Curves(plan, "PROF_GROUND"));
        Assert.Equal(new[] { 20.0, 40.0 }, line.Points.Select(p => p.X));
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
        Assert.Null(project.Longitudinal[0].Ground);
        Assert.Null(project.Longitudinal[3].Ground);
    }

    [Fact]
    public void MissingCrossSectionElevationBreaksExistingTerrain()
    {
        var project = Cross(new() { Offset = -4, Existing = 10 }, new() { Offset = -2, Existing = 11 },
            new() { Offset = 0 }, new() { Offset = 2, Existing = 13 }, new() { Offset = 4, Existing = 14 });
        var lines = Curves(ProfileEngine.Build(project), "PROF_EXISTING");
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { 0.0, 2.0 }, lines[0].Points.Select(p => p.X));
        Assert.Equal(new[] { 6.0, 8.0 }, lines[1].Points.Select(p => p.X));
    }

    [Fact]
    public void ProjectAndBanksHaveDistinctEditableLayerStyles()
    {
        var project = Longitudinal(10, 12);
        foreach (var point in project.Longitudinal)
        {
            point.ProjectBottom = 5;
            point.LeftBank = 13;
            point.RightBank = 14;
        }
        var plan = ProfileEngine.Build(project);
        var design = Assert.Single(Curves(plan, "PROF_PROJECT"));
        Assert.Equal(1, design.ColorIndex);
        Assert.Equal(0.5, design.LineWeightMm, 8);
        Assert.Equal(7, Assert.Single(Curves(plan, "PROF_LEFT_BANK")).ColorIndex);
        Assert.False(Assert.Single(Curves(plan, "PROF_LEFT_BANK")).Dashed);
        var right = Assert.Single(Curves(plan, "PROF_RIGHT_BANK"));
        Assert.Equal(6, right.ColorIndex);
        Assert.True(right.Dashed);
        Assert.Equal(7, Assert.Single(Curves(plan, "PROF_GROUND")).ColorIndex);
    }

    [Fact]
    public void TextSizeUsesPaperScaleAndRequestedWidthFactor()
    {
        var project = Longitudinal(10, 12);
        project.Settings.TextHeightMm = 3;
        project.Settings.TextWidthFactor = 0.72;
        var first = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        project.Settings.MetresPerDrawingUnit = 0.001;
        var second = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Text, second[i].Text);
            Assert.Equal(first[i].Height * 1000, second[i].Height, 7);
            Assert.Equal(0.72, second[i].WidthFactor, 8);
        }
    }

    [Fact]
    public void MoreThanTenThousandMeasuredPointsIsRejected()
    {
        var project = Longitudinal(10, 12);
        project.Longitudinal = Enumerable.Range(0, 10001)
            .Select(i => new LongitudinalPoint { Station = i, Ground = 10 }).ToList();
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }

    [Fact]
    public void NonFiniteElevationIsRejected()
    {
        var project = Longitudinal(10, double.NaN);
        Assert.Throws<ProfileValidationException>(() => ProfileEngine.Build(project));
    }
}
