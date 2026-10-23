using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class HatchPatternGeometryTests
{
    [Fact]
    public void AcuteChannelCornersKeepOrdinaryReadablePitch()
    {
        var polygon = UnevenChannel();
        var original = polygon.Select(p => (p.X, p.Y)).ToArray();
        Assert.Equal(.12, HatchPatternGeometry.NormalSpacing(polygon, 45, .12), 12);
        Assert.Equal(original, polygon.Select(p => (p.X, p.Y)).ToArray());
        Assert.Equal(2.773148148148148, HatchBoundaryGeometry.Area(polygon), 10);
    }

    [Fact]
    public void CompleteNativeEdgesRetainTheMissingLeftTriangle()
    {
        var polygon = UnevenChannel();
        var edges = HatchBoundaryGeometry.ClosedEdges(polygon);
        Assert.Equal(polygon.Count, edges.Count);
        for (int index = 0; index < edges.Count; index++)
        {
            var next = edges[(index + 1) % edges.Count];
            Assert.Equal(edges[index].End.X, next.Start.X);
            Assert.Equal(edges[index].End.Y, next.Start.Y);
        }
        // Omitting the closing edge makes the first visible 45-degree stripe
        // pass through the last surface point, matching the reported blank wedge.
        var last = polygon[polygon.Count - 1];
        double intercept = last.Y - last.X;
        double crossingX = (17.0 / 3 - intercept) / (5.0 / 3);
        Assert.Equal(6.38, crossingX, 10);
        var missingWedge = new[] { polygon[0], last, new Point2(crossingX, crossingX + intercept) };
        double missingArea = HatchBoundaryGeometry.Area(missingWedge);
        Assert.Equal(.5694814814814815, missingArea, 10);
        Assert.True(missingArea > HatchBoundaryGeometry.Area(polygon) * .2);
        Assert.Equal(last.X, edges[edges.Count - 1].Start.X);
        Assert.Equal(polygon[0].X, edges[edges.Count - 1].End.X);
        Assert.Equal(polygon[0].Y, edges[edges.Count - 1].End.Y);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.19)]
    [InlineData(.51)]
    [InlineData(.99)]
    public void NormalPitchHasActualStrokesThroughoutLeftWedge(double phase)
    {
        const double spacing = .12;
        double shift = phase * spacing / Math.Sqrt(2);
        var polygon = UnevenChannel().Select(p => new Point2(p.X - shift, p.Y + shift)).ToList();
        var strokes = HatchPatternGeometry.Clip(polygon, 45, spacing);
        Assert.True(strokes.Count(stroke => Math.Min(stroke.Start.X, stroke.End.X) + shift < 6.38) >= 5);
        Assert.Contains(strokes, stroke => Math.Min(stroke.Start.X, stroke.End.X) + shift < 5.5);
    }

    [Fact]
    public void ExplicitClosureIsIdempotentWithRepeatedAndConsecutiveVertices()
    {
        var polygon = UnevenChannel();
        var repeated = polygon.ToList();
        repeated.Insert(2, repeated[1]); repeated.Add(repeated[0]);
        var before = HatchBoundaryGeometry.ClosedEdges(polygon);
        var after = HatchBoundaryGeometry.ClosedEdges(repeated);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Select(edge => (edge.Start.X, edge.Start.Y, edge.End.X, edge.End.Y)),
            after.Select(edge => (edge.Start.X, edge.Start.Y, edge.End.X, edge.End.Y)));
    }

    [Fact]
    public void ClosedEdgesPreserveConcaveNotchAndVerticalSiltStep()
    {
        var polygon = new List<Point2>
        {
            new(0, 0), new(10, 0), new(10, 3), new(6, 3), new(6, 1), new(4, 1), new(4, 3), new(0, 3)
        };
        var edges = HatchBoundaryGeometry.ClosedEdges(polygon);
        Assert.Equal(26, HatchBoundaryGeometry.Area(edges.Select(edge => edge.Start).ToList()), 12);
        Assert.Contains(edges, edge => edge.Start.X == 6 && edge.End.X == 6 && edge.Start.Y == 3 && edge.End.Y == 1);
        Assert.Contains(edges, edge => edge.Start.X == 4 && edge.End.X == 4 && edge.Start.Y == 1 && edge.End.Y == 3);
    }

    [Fact]
    public void ClosureAndSpacingSurviveRotationScaleAndFarPlacement()
    {
        const double scale = 1000, radians = 31 * Math.PI / 180;
        var polygon = UnevenChannel().Select(p => new Point2(
            scale * (p.X * Math.Cos(radians) - p.Y * Math.Sin(radians)) + 123456,
            scale * (p.X * Math.Sin(radians) + p.Y * Math.Cos(radians)) - 765432)).ToList();
        Assert.Equal(120, HatchPatternGeometry.NormalSpacing(polygon, 76, 120), 10);
        var edges = HatchBoundaryGeometry.ClosedEdges(polygon);
        double area = HatchBoundaryGeometry.Area(edges.Select(edge => edge.Start).ToList());
        double expected = 2.773148148148148 * scale * scale;
        Assert.InRange(Math.Abs(area - expected), 0, expected * 1e-9);
    }

    [Fact]
    public void TinyWholeIslandCanUseSmallerPitchWithoutDensifyingOrdinaryRegions()
    {
        var polygon = new List<Point2> { new(0, 0), new(.01, 0), new(.01, .01), new(0, .01) };
        double spacing = HatchPatternGeometry.NormalSpacing(polygon, 45, .12);
        Assert.InRange(spacing, 0.004, .005);
        Assert.NotEmpty(HatchPatternGeometry.Clip(polygon, 45, spacing));
    }

    [Fact]
    public void NonzeroThinTriangleIsNeverSimplifiedAway()
    {
        var polygon = new List<Point2> { new(0, 0), new(1, 1), new(2, 2 + 1e-14) };
        double spacing = HatchPatternGeometry.NormalSpacing(polygon, 45, .12);
        Assert.True(spacing > 0 && !double.IsNaN(spacing));
        Assert.NotEmpty(HatchPatternGeometry.Clip(polygon, 45, spacing));
        Assert.Equal(3, HatchBoundaryGeometry.ClosedEdges(polygon).Count);
    }

    [Fact]
    public void PatternPhaseIsCommonAcrossIndependentlyClippedRegions()
    {
        var left = new List<Point2> { new(0, 0), new(1, 0), new(1, 2), new(0, 2) };
        var right = new List<Point2> { new(1, 0), new(3, 0), new(3, 2), new(1, 2) };
        foreach (var stroke in HatchPatternGeometry.Clip(left, 45, .17).Concat(HatchPatternGeometry.Clip(right, 45, .17)))
        {
            double across = (-stroke.Start.X + stroke.Start.Y) / Math.Sqrt(2);
            Assert.InRange(Math.Abs(across / .17 - Math.Round(across / .17)), 0, 1e-12);
        }
    }

    private static List<Point2> UnevenChannel() => new()
    {
        new(95.0 / 18, 58.0 / 27), new(7, 1), new(7.5, 1), new(8, 1),
        new(155.0 / 16, 17.0 / 8), new(8, 71.0 / 35), new(7.5, 2), new(7, 61.0 / 30)
    };
}
