using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class AreaCalloutPlacementTests
{
    [Theory]
    [InlineData(4, .05, 100, 1)]
    [InlineData(4, .05, 250, 1)]
    [InlineData(8, .5, 250, 1)]
    [InlineData(20, .05, 250, 1)]
    [InlineData(4, .05, 1000, 1)]
    [InlineData(4, .05, 250, .001)]
    public void ShallowCutAndFillKeepLocalLeadersEvenWhenDimensionCoversPreferredAnchor(
        double width, double depth, double scale, double metresPerUnit)
    {
        var project = ShallowSection(width, depth, scale, metresPerUnit);
        var original = ProjectFile.Serialize(project);
        var plan = ProfileEngine.Build(project);

        Assert.DoesNotContain(plan.Warnings, warning => warning.IndexOf("выноск", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Equal(original, ProjectFile.Serialize(project));
        var areas = Earthworks.Calculate(project.Sections[0]);
        Assert.Equal(width * depth / 4, areas.CutArea!.Value, 9);
        Assert.Equal(width * depth / 4, areas.FillArea!.Value, 9);
        AssertLocalCaptionsAndLeaders(plan, width / metresPerUnit, (1 + depth) / metresPerUnit, scale / (1000 * metresPerUnit));

        // At 1:250 the old fixed cut midpoint was inside the dimension caption,
        // so every route failed and the fallback put the label >250 m away.
        if (scale == 250)
        {
            var dimension = Assert.Single(plan.Entities.OfType<DrawDimension>());
            var cut = Assert.Single(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_LEADER" && line.ColorIndex == 1);
            string value = ((dimension.SecondPoint.X - dimension.FirstPoint.X) * dimension.MeasurementFactor).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            double centerX = (dimension.FirstPoint.X + dimension.SecondPoint.X) / 2;
            double centerY = dimension.DimensionLinePoint.Y + .7 * dimension.TextHeight;
            double halfWidth = value.Length * dimension.TextHeight * .75 * dimension.TextWidthFactor / 2;
            double halfHeight = dimension.TextHeight * 1.3 / 2;
            Assert.False(Math.Abs(cut.Points[0].X - centerX) <= halfWidth && Math.Abs(cut.Points[0].Y - centerY) <= halfHeight);
        }
    }

    [Fact]
    public void CompletelyCoveredTinyAreaReportsRouteConflictWithoutMovingCaptionFarAway()
    {
        var plan = ProfileEngine.Build(ShallowSection(.1, .05, 250, 1));
        Assert.Contains(plan.Warnings, warning => warning.Contains("пересекает подпись у области"));
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("недостаточно свободного места"));
        AssertLocalCaptionsAndLeaders(plan, .1, 1.05, .25);
    }

    [Fact]
    public void CutFillAndSiltHaveSeparateNearbyCaptionsWithAnchorsInsideMatchingHatches()
    {
        var project = ShallowSection(4, 2, 100, 1);
        project.Sections[0].Points = new List<SectionPoint>
        {
            new() { Offset = 0, Existing = 10, Design = 8 },
            new() { Offset = 4, Existing = 10, Design = 12 }
        };
        project.Settings.Datum = 7;
        project.Sections[0].SiltThickness = .4;
        project.Sections[0].SiltLeft = 0;
        project.Sections[0].SiltRight = 4;
        var plan = ProfileEngine.Build(project);
        Assert.DoesNotContain(plan.Warnings, warning => warning.IndexOf("выноск", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Equal(3, plan.Entities.OfType<DrawText>().Count(text => text.Text.StartsWith("F ")));
        AssertLocalCaptionsAndLeaders(plan, 4, 5, .1);
    }

    private static ProfileProject ShallowSection(double width, double depth, double scale, double metresPerUnit) => new()
    {
        Kind = ProfileKind.CrossSection,
        Settings = new ProfileSettings
        {
            HorizontalScale = scale, VerticalScale = scale, Datum = 9,
            MetresPerDrawingUnit = metresPerUnit
        },
        Sections = new List<CrossSection>
        {
            new()
            {
                DesignMode = SectionDesignMode.Points,
                Points = new List<SectionPoint>
                {
                    new() { Offset = 0, Existing = 10 + depth, Design = 10 },
                    new() { Offset = width / 2, Existing = 10, Design = 10 },
                    new() { Offset = width, Existing = 10, Design = 10 + depth }
                }
            }
        }
    };

    private static void AssertLocalCaptionsAndLeaders(DrawingPlan plan, double profileWidth, double profileTop, double paper)
    {
        var texts = plan.Entities.OfType<DrawText>().Where(text => text.Text.StartsWith("F ")).ToList();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            double width = text.Text.Length * text.Height * .75 * text.WidthFactor + 2 * paper;
            var leader = Assert.Single(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_LEADER" && line.ColorIndex == text.ColorIndex);
            Assert.InRange(text.Position.X, -width - 8 * paper, profileWidth + 8 * paper);
            Assert.InRange(text.Position.Y, 0, profileTop + 24 * paper);
            Assert.All(leader.Points, point =>
            {
                Assert.InRange(point.X, -width - 8 * paper, profileWidth + width + 8 * paper);
                Assert.InRange(point.Y, 0, profileTop + 24 * paper);
            });
            var regions = plan.Entities.OfType<DrawHatch>().Where(hatch => hatch.ColorIndex == text.ColorIndex);
            Assert.Contains(regions, region => Inside(region.Boundary, leader.Points[0]));
            Assert.Equal(leader.Points[^2].Y, leader.Points[^1].Y, 9);
            Assert.NotEqual(leader.Points[^2].X, leader.Points[^1].X);
        }
        for (int first = 0; first < texts.Count; first++)
            for (int second = first + 1; second < texts.Count; second++)
            {
                var a = texts[first]; var b = texts[second];
                double aw = a.Text.Length * a.Height * .75 * a.WidthFactor;
                double bw = b.Text.Length * b.Height * .75 * b.WidthFactor;
                bool overlapX = a.Position.X < b.Position.X + bw && a.Position.X + aw > b.Position.X;
                bool overlapY = Math.Abs(a.Position.Y - b.Position.Y) < .65 * (a.Height + b.Height);
                Assert.False(overlapX && overlapY);
            }
    }

    private static bool Inside(List<Point2> boundary, Point2 point)
    {
        bool inside = false;
        for (int index = 0, previous = boundary.Count - 1; index < boundary.Count; previous = index++)
        {
            var a = boundary[index]; var b = boundary[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
