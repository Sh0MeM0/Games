using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class HatchPatternGeometryTests
{
    [Fact]
    public void UserChannelCornerGapIsBoundedToOneMillimetreOnPaper()
    {
        var polygon = UserChannel();
        var original = polygon.Select(p => (p.X, p.Y)).ToArray();
        const double paper = .1; // H 1:100, metres in drawing.
        var coverage = HatchPatternGeometry.Coverage(polygon, 45, 1.2 * paper, paper);
        double incidence = Math.Sin((45 - Math.Atan(1 / 1.5) * 180 / Math.PI) * Math.PI / 180);
        Assert.Equal(6.118823416311343, 1.2 / incidence, 9);
        Assert.Equal(.0196116135138184, coverage.Spacing, 12);
        Assert.InRange(coverage.MaximumTipGap / paper, .999999999, 1.000000001);
        Assert.False(coverage.DensityLimited);
        Assert.Equal(original, polygon.Select(p => (p.X, p.Y)).ToArray());
        Assert.Equal(2.915, Area(polygon), 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.19)]
    [InlineData(.51)]
    [InlineData(.99)]
    public void ActualStripeEndpointsReachBothDaylightTipsForEveryTestedPhase(double phase)
    {
        var polygon = UserChannel();
        var coverage = HatchPatternGeometry.Coverage(polygon, 45, .12, .1);
        double shift = phase * coverage.Spacing / Math.Sqrt(2);
        polygon = polygon.Select(p => new Point2(p.X - shift, p.Y + shift)).ToList();
        var strokes = HatchPatternGeometry.Clip(polygon, 45, coverage.Spacing);
        var endpoints = strokes.SelectMany(s => new[] { s.Start, s.End }).ToList();
        foreach (var tip in new[] { polygon[0], polygon[3] })
            Assert.InRange(endpoints.Min(p => Distance(p, tip)), 0, .100000001);
    }

    [Fact]
    public void LongShallowTriangleGetsStripesNearItsAcuteTip()
    {
        var polygon = new List<Point2> { new(0, 0), new(100, 0), new(100, 1) };
        var coverage = HatchPatternGeometry.Coverage(polygon, 0, 1.2, .1);
        Assert.False(coverage.DensityLimited);
        Assert.InRange(coverage.MaximumTipGap, .0999999999, .1000000001);
        Assert.InRange(coverage.Spacing, .0009999, .001);
        var shifted = polygon.Select(p => new Point2(p.X, p.Y + coverage.Spacing * .49)).ToList();
        var strokes = HatchPatternGeometry.Clip(shifted, 0, coverage.Spacing);
        Assert.InRange(strokes.SelectMany(s => new[] { s.Start, s.End }).Min(p => Distance(p, shifted[0])), 0, .100000001);
    }

    [Fact]
    public void CoverageIsInvariantUnderScaleRotationAndTranslation()
    {
        var polygon = UserChannel();
        var baseline = HatchPatternGeometry.Coverage(polygon, 45, .12, .1);
        const double scale = 1000, radians = 31 * Math.PI / 180;
        var transformed = polygon.Select(p => new Point2(
            scale * (p.X * Math.Cos(radians) - p.Y * Math.Sin(radians)) + 123456,
            scale * (p.X * Math.Sin(radians) + p.Y * Math.Cos(radians)) - 765432)).ToList();
        var result = HatchPatternGeometry.Coverage(transformed, 76, 120, 100);
        Assert.Equal(baseline.Spacing, result.Spacing / scale, 10);
        Assert.Equal(baseline.MaximumTipGap, result.MaximumTipGap / scale, 10);
    }

    [Fact]
    public void CollinearSurveyKnotsDoNotArtificiallyDensifyPattern()
    {
        var simple = new List<Point2> { new(0, 0), new(2, 0), new(2, 1), new(0, 1) };
        var split = new List<Point2> { new(0, 0), new(1, 0), new(2 - 1e-12, 0), new(2, 0), new(2, 1), new(0, 1) };
        var before = HatchPatternGeometry.Coverage(simple, 45, .12, .1);
        var after = HatchPatternGeometry.Coverage(split, 45, .12, .1);
        Assert.Equal(before.Spacing, after.Spacing, 12);
        Assert.False(after.DensityLimited);
        Assert.Equal(6, split.Count);
    }

    [Fact]
    public void NonzeroThinTriangleIsNeverSimplifiedAway()
    {
        var polygon = new List<Point2> { new(0, 0), new(1, 1), new(2, 2 + 1e-14) };
        var result = HatchPatternGeometry.Coverage(polygon, 45, .12, .1);
        Assert.True(result.Spacing > 0 && !double.IsNaN(result.Spacing));
        Assert.NotEmpty(HatchPatternGeometry.Clip(polygon, 45, result.Spacing));
    }

    [Fact]
    public void PathologicalNearParallelTipHasExplicitBoundedDensity()
    {
        var polygon = new List<Point2> { new(0, 0), new(100, 100.000001), new(100, 0) };
        var result = HatchPatternGeometry.Coverage(polygon, 45, .12, .1);
        Assert.True(result.DensityLimited);
        Assert.True(result.MaximumTipGap > .1);
        Assert.InRange(HatchPatternGeometry.Clip(polygon, 45, result.Spacing).Count, 1, HatchPatternGeometry.MaximumPatternLines);
    }

    [Fact]
    public void SharedPatternPhaseIsGlobalAndSurvivesIndependentBoundaryClipping()
    {
        var left = new List<Point2> { new(0, 0), new(1, 0), new(1, 2), new(0, 2) };
        var right = new List<Point2> { new(1, 0), new(3, 0), new(3, 2), new(1, 2) };
        foreach (var stroke in HatchPatternGeometry.Clip(left, 45, .17).Concat(HatchPatternGeometry.Clip(right, 45, .17)))
        {
            double across = (-stroke.Start.X + stroke.Start.Y) / Math.Sqrt(2);
            Assert.InRange(Math.Abs(across / .17 - Math.Round(across / .17)), 0, 1e-12);
        }
    }

    private static List<Point2> UserChannel() => new()
    {
        new(6.35, 160.20), new(8, 159.10), new(9, 159.10), new(10.65, 160.20)
    };
    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Area(IList<Point2> points)
    {
        double result = 0; var origin = points[0];
        for (int i = 1; i + 1 < points.Count; i++)
            result += (points[i].X - origin.X) * (points[i + 1].Y - origin.Y) -
                (points[i + 1].X - origin.X) * (points[i].Y - origin.Y);
        return Math.Abs(result) / 2;
    }
}
