using System.Collections.Generic;
using Profiles.Core;
using Xunit;

public sealed class ExplicitRowFillTests
{
    private static List<LongitudinalPoint> Points() => new()
    {
        new() { Station = 0, LeftBank = 110, RightBank = 112, Water = 105,
            ExistingBottom = 100, ProjectBottom = 99, BottomWidth = 4, LeftSlope = 1, RightSlope = 2 },
        new() { Station = 20, LeftBank = 108, RightBank = 111, Water = 104,
            ExistingBottom = 98, ProjectBottom = 97, BottomWidth = 6, LeftSlope = 2, RightSlope = 3 },
        new() { Station = 50, RightBank = 120 }
    };

    [Fact]
    public void ExplicitFillContinuesEveryColumnByDistanceAndPreservesManualCells()
    {
        var source = Points();
        var result = ProfileInterpolation.FillMissingLongitudinal(source, 50);
        Assert.Equal(105, result.LeftBank);
        Assert.Equal(120, result.RightBank);
        Assert.Equal(102.5, result.Water);
        Assert.Equal(95, result.ExistingBottom);
        Assert.Equal(94, result.ProjectBottom);
        Assert.Equal(9, result.BottomWidth);
        Assert.Equal(3.5, result.LeftSlope);
        Assert.Equal(4.5, result.RightSlope);
        Assert.Null(source[2].LeftBank);
        Assert.Null(source[2].BottomWidth);
        Assert.Null(result.Ground);
    }

    [Theory]
    [InlineData(-10, 111, 3)]
    [InlineData(10, 109, 5)]
    public void FillsBeforeFirstAndBetweenAnchors(double station, double bank, double width)
    {
        var result = ProfileInterpolation.FillMissingLongitudinal(Points(), station);
        Assert.Equal(bank, result.LeftBank);
        Assert.Equal(width, result.BottomWidth);
    }

    [Fact]
    public void ColumnsUseIndependentAnchorsAndSingleValueIsCarried()
    {
        var source = Points();
        source[0].Ground = 125;
        source[0].PeatDepth = 0;
        source[1].LeftBank = null;
        var result = ProfileInterpolation.FillMissingLongitudinal(source, 50);
        Assert.Equal(125, result.Ground);
        Assert.Equal(0, result.PeatDepth);
        Assert.Equal(110, result.LeftBank);
        Assert.Null(result.PipeInvert);
    }

    [Fact]
    public void ImpossibleDimensionsRemainEmptyAndInvalidInputIsRejected()
    {
        var source = Points();
        source[0].BottomWidth = 6;
        source[1].BottomWidth = 2;
        var result = ProfileInterpolation.FillMissingLongitudinal(source, 50);
        Assert.Null(result.BottomWidth);
        Assert.Equal(105, result.LeftBank);
        source[1].Station = 0;
        Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.FillMissingLongitudinal(source, 50));
    }
}
