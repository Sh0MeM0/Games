using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class EarthworkRegionMergingTests
{
    [Fact]
    public void ContinuousCutAcrossSurveyKnotsProducesOnePhysicalBoundary()
    {
        var section = Section(Enumerable.Range(0, 21).Select(index => (X: (double)index, E: 10.0, D: 9.0)));
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        var boundary = Assert.Single(result.CutRegions);
        Assert.Equal(20, result.CutArea.Value, 10);
        Assert.Equal(20, Area(boundary), 10);
        AssertSimple(boundary);
        Assert.Empty(result.FillRegions);
    }

    [Fact]
    public void PointTouchingCutLobesStaySeparateSimplePolygons()
    {
        var result = Earthworks.Calculate(Section(new[] { (0.0, 2.0, 0.0), (1.0, 0.0, 0.0), (2.0, 2.0, 0.0) }));
        Assert.True(result.IsValid, result.Status);
        Assert.Equal(2, result.CutRegions.Count);
        Assert.Equal(2, result.CutArea.Value, 10);
        Assert.All(result.CutRegions, boundary => { Assert.Equal(1, Area(boundary), 10); AssertSimple(boundary); });
    }

    [Fact]
    public void VerticalSiltStepsJoinWithoutArtificialRampsOrAreaChanges()
    {
        var section = Section(Enumerable.Range(0, 11).Select(index => ((double)index, 10.0, 8.0)));
        section.SiltThickness = .5;
        section.SiltLeft = 2;
        section.SiltRight = 8;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        var cut = Assert.Single(result.CutRegions);
        var silt = Assert.Single(result.SiltRegions);
        Assert.Equal(17, result.CutArea.Value, 10);
        Assert.Equal(17, Area(cut), 10);
        Assert.Equal(3, result.SiltArea.Value, 10);
        Assert.Equal(3, Area(silt), 10);
        foreach (double x in new[] { 2.0, 8.0 })
        {
            Assert.Contains(cut, point => point.X == x && point.Y == 10);
            Assert.Contains(cut, point => point.X == x && point.Y == 9.5);
        }
        AssertSimple(cut);
        AssertSimple(silt);
    }

    [Fact]
    public void SiltStepChangingCutToFillDoesNotBridgeSeparateCutIslands()
    {
        var section = Section(Enumerable.Range(0, 11).Select(index => ((double)index, 10.0, 9.0)));
        section.SiltThickness = 2;
        section.SiltLeft = 2;
        section.SiltRight = 8;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Assert.Equal(2, result.CutRegions.Count);
        Assert.Single(result.FillRegions);
        Assert.Single(result.SiltRegions);
        Assert.Equal(4, result.CutRegions.Sum(Area), 10);
        Assert.Equal(6, result.FillRegions.Sum(Area), 10);
        Assert.Equal(12, result.SiltRegions.Sum(Area), 10);
        Assert.All(result.CutRegions, boundary =>
        {
            Assert.True(boundary.All(point => point.X <= 2) || boundary.All(point => point.X >= 8));
            AssertSimple(boundary);
        });
    }

    [Fact]
    public void AlternatingCrossingsMergeOnlyWithinEachSignLobe()
    {
        var result = Earthworks.Calculate(Section(Enumerable.Range(0, 5)
            .Select(index => ((double)index, index % 2 == 0 ? 2.0 : -2.0, 0.0))));
        Assert.True(result.IsValid, result.Status);
        Assert.Equal(3, result.CutRegions.Count);
        Assert.Equal(2, result.FillRegions.Count);
        Assert.Equal(2, result.CutArea.Value, 10);
        Assert.Equal(2, result.FillArea.Value, 10);
        Assert.Equal(2, result.CutRegions.Sum(Area), 10);
        Assert.Equal(2, result.FillRegions.Sum(Area), 10);
        Assert.All(result.CutRegions.Concat(result.FillRegions), AssertSimple);
    }

    [Fact]
    public void VeryThinSiltRemainsOnePositiveRegionAcrossManyKnots()
    {
        var section = Section(Enumerable.Range(0, 101).Select(index => (10.0 * index, 10.0, 9.0)));
        section.SiltThickness = 1e-8;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        var silt = Assert.Single(result.SiltRegions);
        Assert.InRange(Area(silt), 1e-5 - 2e-12, 1e-5 + 2e-12);
        Assert.Equal(1e-5, result.SiltArea.Value, 12);
        AssertSimple(silt);
    }

    [Fact]
    public void MissingSurveyCoverageDoesNotBecomeAConnectedHatch()
    {
        var section = Section(Enumerable.Range(0, 6).Select(index => ((double)index, 10.0, 9.0)));
        section.Points[2].Existing = null;
        section.SiltThickness = .5;
        var result = Earthworks.Calculate(section);
        Assert.False(result.IsValid);
        Assert.Null(result.CutArea);
        Assert.Null(result.SiltArea);
        Assert.Empty(result.CutRegions);
        Assert.Empty(result.FillRegions);
        Assert.Empty(result.SiltRegions);
    }

    [Fact]
    public void TinyPositiveIntervalsAreNotSnappedAwayWhenMerging()
    {
        var result = Earthworks.Calculate(Section(new[] { (0.0, 1.0, 0.0), (1e-9, 1.0, 0.0), (2e-9, 1.0, 0.0) }));
        Assert.True(result.IsValid, result.Status);
        var cut = Assert.Single(result.CutRegions);
        Assert.InRange(Area(cut), 2e-9 - 1e-20, 2e-9 + 1e-20);
        AssertSimple(cut);
    }

    [Fact]
    public void FloatingDaylightAtSurveyVertexDoesNotCreateDuplicateTableOrdinate()
    {
        var section = new CrossSection
        {
            Name = "Дневная поверхность", DesignMode = SectionDesignMode.Trapezoid,
            ProjectBottom = 9.9, BottomWidth = 1, CenterOffset = .8, LeftSlope = 1, RightSlope = 1,
            Points = new List<SectionPoint>
            {
                new() { Offset = .1, Existing = 10.1 },
                new() { Offset = 1.5, Existing = 10.1 },
                new() { Offset = 10.1, Existing = 10.1 }
            }
        };
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        var display = SectionDisplayGeometry.Build(result);
        Assert.Equal(5, display.Count); // Three survey vertices and two bottom knees.
        var left = Assert.Single(display, point => point.Offset == .1);
        var right = Assert.Single(display, point => point.Offset == 1.5);
        Assert.Equal(10.1, left.Design.Value, 12);
        Assert.Equal(10.1, right.Design.Value, 12);
        Assert.Equal(new[] { .1, 1.5, 10.1 }, section.Points.Select(point => point.Offset));
    }

    [Fact]
    public void DisplayNormalizationRetainsEveryDistinctAuthoredOffset()
    {
        var section = Section(new[] { (0.0, 1.0, 0.0), (1e-16, 1.0, 0.0), (2e-16, 1.0, 0.0) });
        var display = SectionDisplayGeometry.Build(Earthworks.Calculate(section));
        Assert.Equal(section.Points.Select(point => point.Offset), display.Select(point => point.Offset));
    }

    private static CrossSection Section(IEnumerable<(double X, double E, double D)> points) => new()
    {
        Name = "Проверка объединения", DesignMode = SectionDesignMode.Points,
        Points = points.Select(point => new SectionPoint { Offset = point.X, Existing = point.E, Design = point.D }).ToList()
    };

    private static double Area(List<Point2> points)
    {
        var origin = points[0];
        double twice = 0;
        for (int index = 1; index + 1 < points.Count; index++)
            twice += (points[index].X - origin.X) * (points[index + 1].Y - origin.Y) -
                (points[index + 1].X - origin.X) * (points[index].Y - origin.Y);
        return Math.Abs(twice) / 2;
    }

    private static void AssertSimple(List<Point2> points)
    {
        Assert.True(points.Count >= 3);
        Assert.Equal(points.Count, points.Select(point => (point.X, point.Y)).Distinct().Count());
        Assert.True(Area(points) > 0);
        for (int first = 0; first < points.Count; first++)
            for (int second = first + 2; second < points.Count; second++)
            {
                if (first == 0 && second == points.Count - 1) continue;
                var a = points[first]; var b = points[(first + 1) % points.Count];
                var c = points[second]; var d = points[(second + 1) % points.Count];
                double abc = Cross(a, b, c), abd = Cross(a, b, d);
                double cda = Cross(c, d, a), cdb = Cross(c, d, b);
                bool crosses = ((abc < 0 && abd > 0) || (abc > 0 && abd < 0)) &&
                    ((cda < 0 && cdb > 0) || (cda > 0 && cdb < 0));
                Assert.False(crosses, "A hatch boundary must not self-intersect.");
            }
    }

    private static double Cross(Point2 a, Point2 b, Point2 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
