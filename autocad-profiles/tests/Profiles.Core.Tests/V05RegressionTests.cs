using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class V05RegressionTests
{
    [Theory]
    [InlineData(ProfileKind.Longitudinal)]
    [InlineData(ProfileKind.CrossSection)]
    public void VersionThreeProjectRetainsItsOriginalScaleInBothDrawingKinds(ProfileKind previousKind)
    {
        var original = BothKinds();
        original.Kind = previousKind;
        original.Settings.HorizontalScale = 333.123456789;
        original.Settings.VerticalScale = 77.987654321;
        var json = JsonNode.Parse(ProjectFile.Serialize(original))!.AsObject();
        json["SchemaVersion"] = 3;
        var settings = json["Settings"]!.AsObject();
        settings.Remove("CrossHorizontalScale");
        settings.Remove("CrossVerticalScale");

        var restored = ProjectFile.Deserialize(json.ToJsonString());

        Assert.Equal(original.ProjectId, restored.ProjectId);
        Assert.Equal(previousKind, restored.Kind);
        Assert.Equal(4, restored.SchemaVersion);
        Assert.Equal(original.Settings.HorizontalScale, restored.Settings.CrossHorizontalScale);
        Assert.Equal(original.Settings.VerticalScale, restored.Settings.CrossVerticalScale);
        double expectedRise = 2 * 333.123456789 / 77.987654321;
        restored.Kind = ProfileKind.Longitudinal;
        Assert.Equal(expectedRise, CurveRise(ProfileEngine.Build(restored), "PROF_GROUND"), 10);
        restored.Kind = ProfileKind.CrossSection;
        Assert.Equal(expectedRise, CurveRise(ProfileEngine.Build(restored), "PROF_EXISTING"), 10);
        Assert.Null(restored.Longitudinal[1].ProjectBottom);
        Assert.Null(restored.Sections[0].Points[1].DistanceFromPrevious);
    }

    [Fact]
    public void SwitchingDrawingKindUsesItsOwnScaleWithoutReplacingSavedSettings()
    {
        var project = BothKinds();
        project.Settings.HorizontalScale = 900;
        project.Settings.VerticalScale = 150;
        project.Settings.CrossHorizontalScale = 240;
        project.Settings.CrossVerticalScale = 80;
        project.Kind = ProfileKind.Longitudinal;
        Assert.Equal(12, CurveRise(ProfileEngine.Build(project), "PROF_GROUND"), 10);
        project.Kind = ProfileKind.CrossSection;
        Assert.Equal(6, CurveRise(ProfileEngine.Build(project), "PROF_EXISTING"), 10);

        ProfileScales.Set(project.Settings, ProfileKind.CrossSection, 500, 100);
        Assert.Equal(10, CurveRise(ProfileEngine.Build(project), "PROF_EXISTING"), 10);
        project.Kind = ProfileKind.Longitudinal;
        Assert.Equal(12, CurveRise(ProfileEngine.Build(project), "PROF_GROUND"), 10);
        var reopened = ProjectFile.Clone(project);
        Assert.Equal(900, reopened.Settings.HorizontalScale);
        Assert.Equal(150, reopened.Settings.VerticalScale);
        Assert.Equal(500, reopened.Settings.CrossHorizontalScale);
        Assert.Equal(100, reopened.Settings.CrossVerticalScale);
        Assert.Equal(project.ProjectId, reopened.ProjectId);
    }

    [Fact]
    public void RepeatedBuildAndRoundtripPreserveRawScalePrecisionAndInactivePair()
    {
        var project = BothKinds();
        project.Settings.HorizontalScale = 1234.567890123;
        project.Settings.VerticalScale = 234.567890123;
        project.Settings.CrossHorizontalScale = 145.678901234;
        project.Settings.CrossVerticalScale = 45.678901234;
        project.Sections[0].SiltThickness = .1234567890123;
        project.Longitudinal[0].Ground = 10.1234567890123;
        string identity = project.ProjectId;
        for (int turn = 0; turn < 4; turn++)
        {
            project.Kind = turn % 2 == 0 ? ProfileKind.CrossSection : ProfileKind.Longitudinal;
            ProfileEngine.Build(project);
            project = ProjectFile.Deserialize(ProjectFile.Serialize(project));
        }

        Assert.Equal(identity, project.ProjectId);
        Assert.Equal(1234.567890123, project.Settings.HorizontalScale);
        Assert.Equal(234.567890123, project.Settings.VerticalScale);
        Assert.Equal(145.678901234, project.Settings.CrossHorizontalScale);
        Assert.Equal(45.678901234, project.Settings.CrossVerticalScale);
        Assert.Equal(.1234567890123, project.Sections[0].SiltThickness);
        Assert.Equal(10.1234567890123, project.Longitudinal[0].Ground);
        Assert.Null(project.Longitudinal[1].ProjectBottom);
    }

    [Fact]
    public void SeparateProfileScalesDoNotChangePhysicalEarthworkQuantities()
    {
        var project = BothKinds();
        var second = ProfileInterpolation.CloneSection(project.Sections[0]);
        project.Sections[0].Station = 0;
        second.Station = 17.123456789;
        project.Sections.Add(second);
        var original = Earthworks.CalculateVolumes(project);
        ProfileScales.Set(project.Settings, ProfileKind.Longitudinal, 10000, 1);
        ProfileScales.Set(project.Settings, ProfileKind.CrossSection, 1, 10000);
        var changed = Earthworks.CalculateVolumes(project);

        Assert.Equal(10, original.Stations[0].Result.CutArea);
        Assert.Equal(171.23456789, original.TotalCutVolume!.Value, 9);
        Assert.Equal(original.TotalCutVolume, changed.TotalCutVolume);
        Assert.Equal(original.TotalFillVolume, changed.TotalFillVolume);
        Assert.Equal(original.TotalSiltVolume, changed.TotalSiltVolume);
    }

    [Fact]
    public void ScreenshotTrapezoidHatchRetainsBothDaylightCornersAndFullArea()
    {
        var project = ScreenshotSection();
        var plan = ProfileEngine.Build(project);
        var hatch = Assert.Single(plan.Entities.OfType<DrawHatch>(), item => item.Layer == "PROF_CUT_HATCH");
        Assert.Contains(hatch.Boundary, point => Near(point.X, 6.35) && Near(point.Y, 2.2));
        Assert.Contains(hatch.Boundary, point => Near(point.X, 8) && Near(point.Y, 1.1));
        Assert.Contains(hatch.Boundary, point => Near(point.X, 9) && Near(point.Y, 1.1));
        Assert.Contains(hatch.Boundary, point => Near(point.X, 10.65) && Near(point.Y, 2.2));
        Assert.Equal(2.915, Area(hatch.Boundary), 10);
        Assert.Equal(2.915, Earthworks.Calculate(project.Sections[0]).CutArea!.Value, 10);
    }

    [Fact]
    public void CompactCrossAnnotationsUsePhysicalValuesAndDisplayedSlopeAngles()
    {
        var project = ScreenshotSection();
        project.Settings.CrossHorizontalScale = 300;
        var plan = ProfileEngine.Build(project);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        var station = Assert.Single(texts, text => text.Text == "ПК1+20.00");
        var horizontal = Assert.Single(texts, text => text.Text == "ГОР.1:300");
        var vertical = Assert.Single(texts, text => text.Text == "ВЕРТ.1:100");
        Assert.True(station.Position.X < 0 && horizontal.Position.X < 0 && vertical.Position.X < 0);
        Assert.True(station.Position.Y > horizontal.Position.Y && horizontal.Position.Y > vertical.Position.Y && vertical.Position.Y > 0);
        Assert.DoesNotContain(texts, text => text.Text == project.Settings.Title);

        var slopes = texts.Where(text => text.Text == "1:1.5").OrderBy(text => text.RotationDegrees).ToList();
        Assert.Equal(2, slopes.Count);
        double angle = Math.Atan(3.0 / 1.5) * 180 / Math.PI;
        Assert.Equal(-angle, slopes[0].RotationDegrees, 9);
        Assert.Equal(angle, slopes[1].RotationDegrees, 9);
        Assert.All(slopes, text => Assert.Equal(1, text.ColorIndex));

        var dimension = Assert.Single(plan.Entities.OfType<DrawDimension>());
        Assert.Equal("PROF_DIMENSION", dimension.Layer);
        Assert.Equal(1, (dimension.SecondPoint.X - dimension.FirstPoint.X) * dimension.MeasurementFactor, 10);
        Assert.Equal(1, dimension.ColorIndex);
        Assert.Equal(8.5, dimension.DimensionLinePoint.X, 10);
        Assert.Equal(2, dimension.DecimalPlaces);
        Assert.DoesNotContain(plan.Entities, entity => entity.Layer == "PROF_DIMENSION" && entity is not DrawDimension);

        var waterLine = Assert.Single(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_WATER");
        var waterText = Assert.Single(texts, text => text.Text == "УР.ВОД.160.70");
        Assert.Equal(5, waterText.ColorIndex);
        Assert.True(waterText.Position.Y > waterLine.Points[0].Y);
        Assert.InRange(waterText.Position.Y - waterLine.Points[0].Y, 0, 2 * waterText.Height);
        Assert.DoesNotContain(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_LEADER" && line.ColorIndex == 5);
    }

    [Fact]
    public void ReferenceAreaAnnotationStaysNearWaterInsteadOfMovingFarAboveProfile()
    {
        var project = ScreenshotSection();
        var section = project.Sections[0];
        section.CenterOffset = 0;
        section.Points = new List<SectionPoint>
        {
            new() { Offset = -6, Existing = 161.28 }, new() { Offset = -3, Existing = 160.20 },
            new() { Offset = 3, Existing = 160.20 }, new() { Offset = 7, Existing = 162.14 }
        };
        var plan = ProfileEngine.Build(project);
        var areaText = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text.StartsWith("F срезки =", StringComparison.Ordinal));
        var water = Assert.Single(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_WATER");

        Assert.Equal(2.915, Earthworks.Calculate(section).CutArea!.Value, 10);
        Assert.Equal(1, areaText.ColorIndex);
        Assert.InRange(areaText.Position.Y - water.Points[0].Y, 0, 2.5 * areaText.Height);
    }

    [Fact]
    public void HatchClippingSplitsDiagonalAtConcaveNotchInsteadOfBridgingIt()
    {
        var boundary = new List<Point2>
        {
            new(0, 0), new(10, 0), new(10, 10), new(6, 10),
            new(6, 4), new(4, 4), new(4, 10), new(0, 10)
        };
        var diagonal = HatchPatternGeometry.Clip(boundary, 45, 1)
            .Where(stroke => Near(stroke.Start.X, stroke.Start.Y) && Near(stroke.End.X, stroke.End.Y))
            .OrderBy(stroke => Math.Min(stroke.Start.X, stroke.End.X)).ToList();

        Assert.Equal(2, diagonal.Count);
        Assert.Equal(0, Math.Min(diagonal[0].Start.X, diagonal[0].End.X), 8);
        Assert.Equal(4, Math.Max(diagonal[0].Start.X, diagonal[0].End.X), 8);
        Assert.Equal(6, Math.Min(diagonal[1].Start.X, diagonal[1].End.X), 8);
        Assert.Equal(10, Math.Max(diagonal[1].Start.X, diagonal[1].End.X), 8);
    }

    [Theory]
    [InlineData(100, 1, 2.5)]
    [InlineData(100, 1, 10)]
    [InlineData(250, .001, 2.5)]
    [InlineData(250, .001, 10)]
    public void CrossTableRowsKeepRequestedPaperHeights(double horizontal, double metresPerUnit, double textHeight)
    {
        var project = ScreenshotSection();
        project.Settings.CrossHorizontalScale = horizontal;
        project.Settings.MetresPerDrawingUnit = metresPerUnit;
        project.Settings.TextHeightMm = textHeight;
        var plan = ProfileEngine.Build(project);
        double paper = horizontal / (1000 * metresPerUnit);
        double width = 17 / metresPerUnit;
        var horizontalBorders = plan.Entities.OfType<DrawPolyline>()
            .Where(line => line.Layer == "PROF_GRID" && line.Points.Count == 2 &&
                Near(line.Points[0].Y, line.Points[1].Y) && line.Points[0].X < 0 && Near(line.Points[1].X, width))
            .Select(line => line.Points[0].Y / paper).OrderByDescending(value => value).ToList();

        Assert.Equal(5, horizontalBorders.Count);
        var expected = new[] { 0.0, -6, -11, -23, -35 };
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], horizontalBorders[index], 8);
        var distanceRow = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == "РАССТОЯНИЕ, м");
        Assert.InRange(distanceRow.Position.Y / paper, -11, -6);
        var distances = plan.Entities.OfType<DrawText>().Where(text => text.Position.X >= 0 &&
            text.Position.Y < -6 * paper && text.Position.Y > -11 * paper).ToList();
        Assert.NotEmpty(distances);
        Assert.All(distances, text =>
        {
            Assert.True(text.Position.Y - text.Height / 2 >= -11 * paper - 1e-8);
            Assert.True(text.Position.Y + text.Height / 2 <= -6 * paper + 1e-8);
        });
    }

    private static ProfileProject BothKinds() => new()
    {
        Settings = new ProfileSettings
        {
            Datum = 0, HorizontalScale = 1000, VerticalScale = 100,
            CrossHorizontalScale = 100, CrossVerticalScale = 100
        },
        Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, Ground = 10 }, new() { Station = 10, Ground = 12 }
        },
        Sections = new List<CrossSection>
        {
            new()
            {
                Station = 0, DesignMode = SectionDesignMode.Points,
                Points = new List<SectionPoint>
                {
                    new() { Offset = 0, Existing = 10, Design = 9 },
                    new() { Offset = 10, Existing = 12, Design = 11 }
                }
            }
        }
    };

    private static ProfileProject ScreenshotSection() => new()
    {
        Kind = ProfileKind.CrossSection,
        Settings = new ProfileSettings
        {
            Title = "Не нужен отдельный большой заголовок", Datum = 158,
            HorizontalScale = 1000, VerticalScale = 100,
            CrossHorizontalScale = 100, CrossVerticalScale = 100
        },
        Sections = new List<CrossSection>
        {
            new()
            {
                Station = 120, DesignMode = SectionDesignMode.Trapezoid,
                ProjectBottom = 159.1, BottomWidth = 1, CenterOffset = 8.5,
                LeftSlope = 1.5, RightSlope = 1.5, Water = 160.7,
                Points = new List<SectionPoint>
                {
                    new() { Offset = 0, Existing = 160.2 }, new() { Offset = 8.5, Existing = 160.2 },
                    new() { Offset = 17, Existing = 160.2 }
                }
            }
        }
    };

    private static double CurveRise(DrawingPlan plan, string layer)
    {
        var curve = Assert.Single(plan.Entities.OfType<DrawPolyline>(), item => item.Layer == layer);
        return curve.Points[^1].Y - curve.Points[0].Y;
    }

    private static double Area(IList<Point2> points)
    {
        var origin = points[0];
        double twice = 0;
        for (int index = 1; index + 1 < points.Count; index++)
            twice += (points[index].X - origin.X) * (points[index + 1].Y - origin.Y) -
                (points[index + 1].X - origin.X) * (points[index].Y - origin.Y);
        return Math.Abs(twice) / 2;
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < 1e-8;
}
