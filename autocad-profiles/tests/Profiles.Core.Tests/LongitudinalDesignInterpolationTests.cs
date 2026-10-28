using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class LongitudinalDesignInterpolationTests
{
    [Fact]
    public void CapturedInteriorStationResolvesBottomElevationWidthAndBothSlopesWithoutFixingDerivedAnchors()
    {
        var source = Profile();
        string original = ProjectFile.Serialize(source);
        var capture = DrawingSurvey.BuildLongitudinal(new List<LongitudinalSurveyPick>
        {
            new() { Position = new Point2(1000, 500), Values = new LongitudinalPoint { Ground = 14.25 } }
        }, 25, SurveySourceUnits.ModelMetres());

        var merged = DrawingSurvey.MergeLongitudinal(source, capture);
        var resolved = ProfileInterpolation.ResolveLongitudinal(merged).Longitudinal.Single(point => point.Station == 25);
        Assert.Equal(14.25, resolved.Ground);
        Assert.Equal(9, resolved.ExistingBottom);
        Assert.Equal(7.5, resolved.ProjectBottom);
        Assert.Equal(2.5, resolved.BottomWidth);
        Assert.Equal(1.5, resolved.LeftSlope);
        Assert.Equal(3.5, resolved.RightSlope);

        var raw = merged.Longitudinal.Single(point => point.Station == 25);
        Assert.Null(raw.ExistingBottom);
        Assert.Null(raw.ProjectBottom);
        Assert.Null(raw.BottomWidth);
        Assert.Null(raw.LeftSlope);
        Assert.Null(raw.RightSlope);
        Assert.Equal(original, ProjectFile.Serialize(source));
        Assert.Null(capture[0].BottomWidth);

        // Resaving/reopening and changing a neighbour must recalculate the
        // inserted point rather than retaining yesterday's interpolated width.
        merged = ProjectFile.Deserialize(ProjectFile.Serialize(merged));
        merged.Longitudinal.Single(point => point.Station == 100).BottomWidth = 10;
        Assert.Equal(4, ProfileInterpolation.SampleLongitudinal(merged.Longitudinal, 25).BottomWidth);
    }

    [Fact]
    public void SparseDesignFieldsUseTheirOwnAnchorStationsAndRetainMeasuredInteriorValues()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = 0, BottomWidth = 2, LeftSlope = 1 },
            new() { Station = 20, RightSlope = 2 },
            new() { Station = 40, BottomWidth = 7.123456789 },
            new() { Station = 80, LeftSlope = 5 },
            new() { Station = 100, BottomWidth = 10, RightSlope = 6 }
        };

        var resolved = ProfileInterpolation.ResolveLongitudinal(source);
        var exact = ProfileInterpolation.SampleLongitudinal(source, 40);
        Assert.Equal(7.123456789, exact.BottomWidth);
        Assert.Equal(3, exact.LeftSlope);
        Assert.Equal(3, exact.RightSlope);
        Assert.Equal(exact.BottomWidth, resolved[2].BottomWidth);
        Assert.Equal(exact.LeftSlope, resolved[2].LeftSlope);
        Assert.Equal(exact.RightSlope, resolved[2].RightSlope);
        Assert.Equal(4.5617283945, resolved[1].BottomWidth!.Value, 10);
        Assert.Null(source[2].LeftSlope);
        Assert.Null(source[2].RightSlope);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(110)]
    public void SamplingContinuesEndElevationsButDoesNotExtendDimensions(double station)
    {
        var sample = ProfileInterpolation.SampleLongitudinal(Profile().Longitudinal, station);
        if (station < 0)
        {
            Assert.Null(sample.ExistingBottom);
            Assert.Null(sample.ProjectBottom);
        }
        else
        {
            Assert.Equal(12.4, sample.ExistingBottom!.Value, 10);
            Assert.Equal(9.2, sample.ProjectBottom!.Value, 10);
        }
        Assert.Null(sample.BottomWidth);
        Assert.Null(sample.LeftSlope);
        Assert.Null(sample.RightSlope);

        // Only the explicit new-section suggestion API carries endpoint
        // dimensions; elevation continuation belongs to SampleLongitudinal.
        var seed = ProfileInterpolation.SampleDesignParameters(Profile().Longitudinal, station);
        Assert.Equal(station < 0 ? 2 : 4, seed.BottomWidth);
        Assert.Equal(station < 0 ? 1 : 3, seed.LeftSlope);
        Assert.Equal(station < 0 ? 4 : 2, seed.RightSlope);
        Assert.Null(seed.ProjectBottom);
    }

    [Fact]
    public void NewSectionAtIntermediateStationReceivesTheSameCalculatedDesign()
    {
        var section = ProfileInterpolation.CreateSection(Profile().Longitudinal, 25);
        Assert.Equal(7.5, section.ProjectBottom);
        Assert.Equal(2.5, section.BottomWidth);
        Assert.Equal(1.5, section.LeftSlope);
        Assert.Equal(3.5, section.RightSlope);
        Assert.Null(section.Points[1].DistanceFromPrevious);
        Assert.Null(section.Points[2].DistanceFromPrevious);
    }

    [Fact]
    public void ImportedManualDimensionsWinAndUnknownFieldsRemainUnknown()
    {
        var source = Profile();
        source.Longitudinal.ForEach(point => point.RightSlope = null);
        var result = DrawingSurvey.MergeLongitudinal(source, new List<LongitudinalPoint>
        {
            new() { Station = 25, BottomWidth = 9.87654321, LeftSlope = 2.125 }
        });
        var point = ProfileInterpolation.ResolveLongitudinal(result).Longitudinal[1];
        Assert.Equal(9.87654321, point.BottomWidth);
        Assert.Equal(2.125, point.LeftSlope);
        Assert.Null(point.RightSlope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDesignAnchorsAreRejectedBeforeInterpolation(double invalid)
    {
        foreach (Action<LongitudinalPoint> assign in new Action<LongitudinalPoint>[]
        {
            point => point.BottomWidth = invalid,
            point => point.LeftSlope = invalid,
            point => point.RightSlope = invalid
        })
        {
            var source = Profile().Longitudinal;
            assign(source[0]);
            Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.SampleLongitudinal(source, 25));
            Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.ResolveLongitudinal(source));
        }
    }

    private static ProfileProject Profile() => new()
    {
        Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, ExistingBottom = 8, ProjectBottom = 7, BottomWidth = 2, LeftSlope = 1, RightSlope = 4 },
            new() { Station = 100, ExistingBottom = 12, ProjectBottom = 9, BottomWidth = 4, LeftSlope = 3, RightSlope = 2 }
        }
    };
}
