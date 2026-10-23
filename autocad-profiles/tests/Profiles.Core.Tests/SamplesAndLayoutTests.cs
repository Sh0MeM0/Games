using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class SamplesAndLayoutTests
{
    [Theory]
    [InlineData(ProfileKind.Longitudinal)]
    [InlineData(ProfileKind.CrossSection)]
    public void BuiltInSamplesProduceFiniteEditableEntities(ProfileKind kind)
    {
        var sample = kind == ProfileKind.Longitudinal ? SampleProjects.Longitudinal() : SampleProjects.CrossSections();
        Assert.Equal(kind, sample.Kind);
        var plan = ProfileEngine.Build(sample);
        Assert.True(double.IsFinite(plan.Datum));
        Assert.NotEmpty(plan.Entities.OfType<DrawPolyline>());
        Assert.NotEmpty(plan.Entities.OfType<DrawText>());
        foreach (var entity in plan.Entities)
        {
            Assert.StartsWith("PROF_", entity.Layer);
            Assert.True(double.IsFinite(entity.LineWeightMm));
            switch (entity)
            {
                case DrawPolyline line:
                    Assert.True(line.Points.Count >= 2);
                    Assert.All(line.Points, FinitePoint);
                    break;
                case DrawText text:
                    FinitePoint(text.Position);
                    Assert.False(string.IsNullOrWhiteSpace(text.Text));
                    Assert.True(double.IsFinite(text.Height) && text.Height > 0);
                    Assert.True(double.IsFinite(text.WidthFactor) && text.WidthFactor > 0);
                    Assert.True(double.IsFinite(text.MaxWidth) && text.MaxWidth >= 0);
                    break;
                case DrawCircle circle:
                    FinitePoint(circle.Center);
                    Assert.True(double.IsFinite(circle.Radius) && circle.Radius > 0);
                    break;
                case DrawDimension dimension:
                    FinitePoint(dimension.FirstPoint);
                    FinitePoint(dimension.SecondPoint);
                    FinitePoint(dimension.DimensionLinePoint);
                    Assert.True(dimension.SecondPoint.X > dimension.FirstPoint.X);
                    Assert.Equal(dimension.FirstPoint.Y, dimension.SecondPoint.Y);
                    Assert.True(double.IsFinite(dimension.TextHeight) && dimension.TextHeight > 0);
                    Assert.True(double.IsFinite(dimension.TextWidthFactor) && dimension.TextWidthFactor > 0);
                    Assert.True(double.IsFinite(dimension.MeasurementFactor) && dimension.MeasurementFactor > 0);
                    Assert.Equal(2, dimension.DecimalPlaces);
                    break;
                case DrawHatch hatch:
                    Assert.True(hatch.Boundary.Count >= 3);
                    Assert.All(hatch.Boundary, FinitePoint);
                    Assert.True(double.IsFinite(hatch.PatternSpacing) && hatch.PatternSpacing > 0);
                    break;
                default:
                    Assert.Fail("Unexpected entity type: " + entity.GetType().Name);
                    break;
            }
        }
    }

    private static void FinitePoint(Point2 point)
    {
        Assert.True(double.IsFinite(point.X));
        Assert.True(double.IsFinite(point.Y));
    }

    [Fact]
    public void SampleProjectsAreIndependentEditableInstances()
    {
        var first = SampleProjects.Longitudinal();
        var second = SampleProjects.Longitudinal();
        var original = second.Longitudinal[0].Ground;
        first.Longitudinal[0].Ground = -999;
        first.Settings.Title = "Изменённый экземпляр";
        Assert.Equal(original, second.Longitudinal[0].Ground);
        Assert.NotEqual(first.Settings.Title, second.Settings.Title);
    }

    [Fact]
    public void LongitudinalTitleIsAboveTheHighestDrawnElevation()
    {
        var project = Longitudinal(10, 12);
        project.Longitudinal[0].RightBank = 100;
        project.Longitudinal[1].RightBank = 101;
        var plan = ProfileEngine.Build(project);
        var title = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == project.Settings.Title);
        var highest = Curves(plan, "PROF_RIGHT_BANK").SelectMany(line => line.Points).Max(point => point.Y);
        Assert.True(title.Position.Y > highest);
    }

    [Fact]
    public void EmptyOptionalRowsCanBeShownWithoutAddingFalseGeometry()
    {
        var project = Longitudinal(10, 12);
        var compact = ProfileEngine.Build(project);
        project.Settings.ShowEmptyRows = true;
        var expanded = ProfileEngine.Build(project);
        Assert.True(expanded.Entities.OfType<DrawText>().Count() > compact.Entities.OfType<DrawText>().Count());
        Assert.Empty(Curves(expanded, "PROF_PROJECT"));
        Assert.Empty(Curves(expanded, "PROF_WATER"));
        Assert.Empty(Curves(expanded, "PROF_LEFT_BANK"));
        Assert.Empty(Curves(expanded, "PROF_RIGHT_BANK"));
    }

    [Fact]
    public void TableLabelsAreLeftOfSurveyOriginAndTextReflectsEnteredElevation()
    {
        var project = Longitudinal(10.25, 12.75);
        var texts = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        Assert.Contains(texts, text => text.Position.X < 0);
        Assert.Contains(texts, text => text.Text.Replace(',', '.').Contains("10.25"));
        Assert.Contains(texts, text => text.Text.Replace(',', '.').Contains("12.75"));
    }

    [Fact]
    public void TenMillimetreTextExpandsTableAndHeaderWithoutChangingProfileGeometry()
    {
        var project = Longitudinal(10, 12);
        var standard = ProfileEngine.Build(project);
        project.Settings.TextHeightMm = 10;
        var large = ProfileEngine.Build(project);
        Assert.Equal(Curves(standard, "PROF_GROUND").Single().Points, Curves(large, "PROF_GROUND").Single().Points);
        var standardLabel = standard.Entities.OfType<DrawText>().Single(t => t.Text == "РАССТОЯНИЯ, М");
        var largeLabel = large.Entities.OfType<DrawText>().Single(t => t.Text == "РАССТОЯНИЯ, М");
        Assert.Equal(standardLabel.Position.X * 4, largeLabel.Position.X, 8);
        Assert.Equal(standardLabel.Position.Y * 4, largeLabel.Position.Y, 8);
        var horizontal = large.Entities.OfType<DrawText>().Single(t => t.Text.StartsWith("ГОР."));
        var vertical = large.Entities.OfType<DrawText>().Single(t => t.Text.StartsWith("ВЕРТ."));
        Assert.True(horizontal.Position.Y - vertical.Position.Y >= 1.5 * horizontal.Height);
        var boundaries = large.Entities.OfType<DrawPolyline>()
            .Where(l => l.Layer == "PROF_GRID" && l.Points.Count == 2 && l.Points[0].X < 0 && l.Points[1].X > 0 && l.Points[0].Y == l.Points[1].Y)
            .Select(l => l.Points[0].Y).Distinct().OrderBy(y => y).ToArray();
        Assert.All(boundaries.Zip(boundaries.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= 20));
    }

    [Theory]
    [InlineData(2.5, 0.85)]
    [InlineData(10, 0.85)]
    [InlineData(10, 3)]
    public void AsymmetricSlopeValuesFitInsideTheirTableRow(double height, double widthFactor)
    {
        var project = SampleProjects.Longitudinal();
        project.Settings.TextHeightMm = height;
        project.Settings.TextWidthFactor = widthFactor;
        var plan = ProfileEngine.Build(project);
        var ratio = plan.Entities.OfType<DrawText>().First(t => t.Text.Contains(" / ") && t.RotationDegrees == 90);
        var borders = plan.Entities.OfType<DrawPolyline>()
            .Where(l => l.Layer == "PROF_GRID" && l.Points.Count == 2 && l.Points[0].X < 0 && l.Points[1].X > 0 && l.Points[0].Y == l.Points[1].Y)
            .Select(l => l.Points[0].Y).ToList();
        double lower = borders.Where(y => y < ratio.Position.Y).Max();
        double upper = borders.Where(y => y > ratio.Position.Y).Min();
        // Arial rendering of the default 2.5 mm label occupied 14.22 mm in the old 6 mm row.
        double measuredSpan = 14.23 * (height / 2.5) * (widthFactor / 0.85);
        Assert.True(ratio.Position.Y - measuredSpan / 2 > lower);
        Assert.True(ratio.Position.Y + measuredSpan / 2 < upper);
    }

    [Fact]
    public void LongWrappedTitleHasClearanceAboveScaleLabels()
    {
        var project = Longitudinal(10, 12);
        project.Settings.Title = string.Join(" ", Enumerable.Repeat("ПРОФИЛЬ", 25));
        var plan = ProfileEngine.Build(project);
        var title = plan.Entities.OfType<DrawText>().Single(t => t.Text == project.Settings.Title);
        var scale = plan.Entities.OfType<DrawText>().Single(t => t.Text.StartsWith("ГОР."));
        Assert.True(title.MaxWidth > 0);
        Assert.True(title.Position.Y - scale.Position.Y >= 4 * title.Height);
    }
}
