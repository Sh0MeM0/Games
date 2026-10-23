using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class V06RegressionTests
{
    [Theory]
    [InlineData(1, 100, 100)]
    [InlineData(.001, 100, 100)]
    [InlineData(.3048, 250, 50)]
    [InlineData(1, 1000, 100)]
    public void NativeBottomDimensionKeepsPhysicalMetresAndUnroundedGeometry(double metresPerUnit, double horizontal, double vertical)
    {
        var project = UnevenSection();
        const double width = 1.234567890123;
        project.Sections[0].BottomWidth = width;
        project.Settings.MetresPerDrawingUnit = metresPerUnit;
        project.Settings.CrossHorizontalScale = horizontal;
        project.Settings.CrossVerticalScale = vertical;
        project.Settings.TextWidthFactor = .73;
        string original = ProjectFile.Serialize(project);

        var plan = ProfileEngine.Build(project);
        var dimension = Assert.Single(plan.Entities.OfType<DrawDimension>());
        double paper = horizontal / (1000 * metresPerUnit);

        Assert.Equal("PROF_DIMENSION", dimension.Layer);
        Assert.Equal(1, dimension.ColorIndex);
        Assert.Equal((7.5 - width / 2) / metresPerUnit, dimension.FirstPoint.X, 8);
        Assert.Equal((7.5 + width / 2) / metresPerUnit, dimension.SecondPoint.X, 8);
        Assert.Equal(horizontal / (metresPerUnit * vertical), dimension.FirstPoint.Y, 8);
        Assert.Equal(dimension.FirstPoint.Y, dimension.SecondPoint.Y);
        Assert.Equal(7.5 / metresPerUnit, dimension.DimensionLinePoint.X, 8);
        Assert.Equal(metresPerUnit, dimension.MeasurementFactor);
        Assert.Equal(width, (dimension.SecondPoint.X - dimension.FirstPoint.X) * dimension.MeasurementFactor, 10);
        Assert.Equal(2.5 * paper, dimension.TextHeight, 8);
        Assert.Equal(.73, dimension.TextWidthFactor);
        Assert.Equal(2, dimension.DecimalPlaces);
        Assert.Single(plan.Entities, entity => entity.Layer == "PROF_DIMENSION");
        Assert.Equal(original, ProjectFile.Serialize(project));
    }

    [Fact]
    public void StackedSectionsTranslateDimensionEndpointsAndLineTogether()
    {
        var project = UnevenSection();
        var second = ProfileInterpolation.CloneSection(project.Sections[0]);
        second.SectionId = Guid.NewGuid().ToString("N");
        second.Station = 220;
        project.Sections.Add(second);

        var plan = ProfileEngine.Build(project);
        var dimensions = plan.Entities.OfType<DrawDimension>().ToList();
        var curves = plan.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_PROJECT").ToList();

        Assert.Equal(2, dimensions.Count);
        Assert.Equal(2, curves.Count);
        double shift = curves[1].Points[0].Y - curves[0].Points[0].Y;
        Assert.True(shift < 0);
        Assert.Equal(shift, dimensions[1].FirstPoint.Y - dimensions[0].FirstPoint.Y, 10);
        Assert.Equal(shift, dimensions[1].SecondPoint.Y - dimensions[0].SecondPoint.Y, 10);
        Assert.Equal(shift, dimensions[1].DimensionLinePoint.Y - dimensions[0].DimensionLinePoint.Y, 10);
        Assert.Equal(dimensions[0].FirstPoint.X, dimensions[1].FirstPoint.X);
        Assert.Equal(dimensions[0].SecondPoint.X, dimensions[1].SecondPoint.X);
        Assert.Equal(dimensions[0].MeasurementFactor, dimensions[1].MeasurementFactor);
    }

    [Fact]
    public void ManualFlatBottomUsesItsFullContinuousWidthAcrossSurveyKnots()
    {
        var project = UnevenSection();
        project.Sections[0] = new CrossSection
        {
            Station = 120, DesignMode = SectionDesignMode.Points,
            Points = new List<SectionPoint>
            {
                new() { Offset = 0, Existing = 162, Design = 161 },
                new() { Offset = 3, Existing = 162, Design = 159 },
                new() { Offset = 5, Existing = 162, Design = 159 },
                new() { Offset = 7, Existing = 162, Design = 159 },
                new() { Offset = 10, Existing = 162, Design = 161 }
            }
        };

        var dimension = Assert.Single(ProfileEngine.Build(project).Entities.OfType<DrawDimension>());

        Assert.Equal(3, dimension.FirstPoint.X);
        Assert.Equal(7, dimension.SecondPoint.X);
        Assert.Equal(4, (dimension.SecondPoint.X - dimension.FirstPoint.X) * dimension.MeasurementFactor);
        Assert.Null(project.Sections[0].BottomWidth);
    }

    [Fact]
    public void VShapedManualProfileDoesNotInventAFlatBottomDimension()
    {
        var project = UnevenSection();
        project.Sections[0] = new CrossSection
        {
            Station = 120, DesignMode = SectionDesignMode.Points,
            Points = new List<SectionPoint>
            {
                new() { Offset = 0, Existing = 162, Design = 161 },
                new() { Offset = 3, Existing = 162, Design = 159 },
                new() { Offset = 10, Existing = 162, Design = 161 }
            }
        };

        Assert.Empty(ProfileEngine.Build(project).Entities.OfType<DrawDimension>());
    }

    [Fact]
    public void UnevenSurveyKeepsLeftCutWedgeAtNormalPaperHatchSpacing()
    {
        var project = UnevenSection();
        var result = Earthworks.Calculate(project.Sections[0]);
        var hatch = Assert.Single(ProfileEngine.Build(project).Entities.OfType<DrawHatch>(), entity => entity.Layer == "PROF_CUT_HATCH");

        // Independent linear intersections: the left survey falls 1/15 m per m,
        // the right rises 2/35; project sides rise 2/3 from bottom knees 7 and 8.
        const double left = 95.0 / 18;
        const double right = 155.0 / 16;
        const double area = 599.0 / 216;
        Assert.Equal(area, result.CutArea!.Value, 10);
        Assert.Contains(hatch.Boundary, point => Math.Abs(point.X - left) < 1e-9 && Math.Abs(point.Y - (160.5 - left / 15 - 158)) < 1e-9);
        Assert.Contains(hatch.Boundary, point => Math.Abs(point.X - right) < 1e-9 && Math.Abs(point.Y - 2.125) < 1e-9);
        Assert.Contains(hatch.Boundary, point => point.X == 7 && point.Y == 1);
        Assert.Contains(hatch.Boundary, point => point.X == 8 && point.Y == 1);
        Assert.Equal(area, PolygonArea(hatch.Boundary), 10);
        Assert.Equal(.12, hatch.PatternSpacing, 10); // 1.2 mm at 1:100 in metre drawing units.

        var strokes = HatchPatternGeometry.Clip(hatch.Boundary, hatch.PatternAngleDegrees, hatch.PatternSpacing);
        var leftWedgeStrokes = strokes.Where(stroke =>
        {
            double x = (stroke.Start.X + stroke.End.X) / 2;
            double y = (stroke.Start.Y + stroke.End.Y) / 2;
            return x > left && x < 6.3 && y > 1 + (7 - x) * 2 / 3 && y < 2.5 - x / 15;
        }).ToList();
        Assert.True(leftWedgeStrokes.Count >= 3, "Normal-spaced strokes must fill the left wedge whose missing closing edge previously cut it away.");
    }

    private static double PolygonArea(IList<Point2> boundary)
    {
        var origin = boundary[0];
        double twice = 0;
        for (int index = 1; index + 1 < boundary.Count; index++)
            twice += (boundary[index].X - origin.X) * (boundary[index + 1].Y - origin.Y)
                - (boundary[index + 1].X - origin.X) * (boundary[index].Y - origin.Y);
        return Math.Abs(twice) / 2;
    }

    private static ProfileProject UnevenSection() => new()
    {
        Kind = ProfileKind.CrossSection,
        Settings = new ProfileSettings
        {
            Datum = 158, HorizontalScale = 1000, VerticalScale = 100,
            CrossHorizontalScale = 100, CrossVerticalScale = 100
        },
        Sections = new List<CrossSection>
        {
            new()
            {
                Station = 120, DesignMode = SectionDesignMode.Trapezoid,
                ProjectBottom = 159, BottomWidth = 1, CenterOffset = 7.5,
                LeftSlope = 1.5, RightSlope = 1.5, Water = 160.5,
                Points = new List<SectionPoint>
                {
                    new() { Offset = 0, Existing = 161 }, new() { Offset = 2, Existing = 160.8 },
                    new() { Offset = 4.5, Existing = 160.2 }, new() { Offset = 7.5, Existing = 160 },
                    new() { Offset = 11, Existing = 160.2 }, new() { Offset = 15.5, Existing = 161 }
                }
            }
        }
    };
}
