using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class SectionApproximationTests
{
    private static CrossSection Draft(int count = 9, int bottom = 4) => new()
    {
        UsePointDistances = true, DesignMode = SectionDesignMode.None,
        Points = Enumerable.Range(0, count).Select(i => new SectionPoint
        {
            Role = i == 0 ? SectionPointRole.LeftBank : i == count - 1 ? SectionPointRole.RightBank :
                i == bottom ? SectionPointRole.Bottom : SectionPointRole.Regular,
            Existing = i == 0 ? 110 : i == count - 1 ? 112 : i == bottom ? 100 : null,
            DistanceFromPrevious = i == 0 ? 0 : null,
            Label = "Точка " + i
        }).ToList()
    };

    [Fact]
    public void WidthPlacesBottomAtMidspanRegardlessOfPointCounts()
    {
        var input = Draft(8, 2);
        var output = SectionApproximation.DistributeDistances(input, 20);
        Assert.Equal(10, output.Points[2].Offset, 10);
        Assert.Equal(20, output.Points[7].Offset, 10);
        Assert.Equal(5, output.Points[1].DistanceFromPrevious);
        Assert.Equal(2, output.Points[3].DistanceFromPrevious);
        Assert.All(input.Points.Skip(1), p => Assert.Null(p.DistanceFromPrevious));
        Assert.Equal(input.SectionId, output.SectionId);
        Assert.Equal("Точка 3", output.Points[3].Label);
    }

    [Fact]
    public void AsymmetricWidthPreservesKnownIntervalsAndOrigin()
    {
        var source = Draft(); source.Points[0].Offset = -7.25;
        source.Points[1].DistanceFromPrevious = 1.23456789;
        source.Points[8].DistanceFromPrevious = 2;
        var output = SectionApproximation.DistributeDistances(source, 22, 7);
        Assert.Equal(1.23456789, output.Points[1].DistanceFromPrevious);
        Assert.Equal(2, output.Points[8].DistanceFromPrevious);
        Assert.Equal(-0.25, output.Points[4].Offset, 10);
        Assert.Equal(14.75, output.Points[8].Offset, 10);
        Assert.All(output.Points.Skip(1), p => Assert.True(p.DistanceFromPrevious > 0));
    }

    [Theory]
    [InlineData(SectionApproximationShape.SmoothBowl)]
    [InlineData(SectionApproximationShape.StraightSlopes)]
    public void EstimatesRiseFromBottomToEachBankWithoutChangingAnchors(SectionApproximationShape shape)
    {
        var source = SectionApproximation.DistributeDistances(Draft(), 23, 8);
        var result = SectionApproximation.FillElevations(source, shape);
        Assert.Equal(110, result.Points[0].Existing);
        Assert.Equal(100, result.Points[4].Existing);
        Assert.Equal(112, result.Points[8].Existing);
        for (int i = 1; i <= 4; i++) Assert.True(result.Points[i].Existing < result.Points[i - 1].Existing);
        for (int i = 5; i <= 8; i++) Assert.True(result.Points[i].Existing > result.Points[i - 1].Existing);
        Assert.Null(source.Points[3].Existing);
        Assert.True(result.Points[3].Existing > 100);
        Assert.True(result.Points[5].Existing > 100);
        var second = SectionApproximation.FillElevations(result, shape);
        Assert.Equal(result.Points.Select(p => p.Existing), second.Points.Select(p => p.Existing));
    }

    [Fact]
    public void SmoothBowlHasGentlerRiseNextToBottomThanStraightSlopes()
    {
        var source = SectionApproximation.DistributeDistances(Draft(), 20);
        var smooth = SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl);
        var straight = SectionApproximation.FillElevations(source, SectionApproximationShape.StraightSlopes);
        Assert.True(smooth.Points[3].Existing < straight.Points[3].Existing);
        Assert.True(smooth.Points[5].Existing < straight.Points[5].Existing);
        Assert.Equal(102.5, straight.Points[3].Existing);
        Assert.Equal(103, straight.Points[5].Existing);
    }

    [Fact]
    public void UnevenDistancesControlElevationsRatherThanColumnNumbers()
    {
        var source = Draft(5, 2);
        source.Points[1].DistanceFromPrevious = 1;
        var spaced = SectionApproximation.DistributeDistances(source, 20);
        var result = SectionApproximation.FillElevations(spaced, SectionApproximationShape.StraightSlopes);
        Assert.Equal(109, result.Points[1].Existing);
        Assert.Equal(106, result.Points[3].Existing);
    }

    [Fact]
    public void ManualAnchorsAndFlatBottomArePreservedIncludingDesign()
    {
        var source = SectionApproximation.DistributeDistances(Draft(13, 6), 24);
        source.Points[2].Existing = 108.12345678;
        source.Points[4].Existing = 100;
        source.Points[8].Existing = 100;
        source.Points[3].Design = 99.45678;
        source.ProjectAnchorIndex = 6; source.CenterOffset = 12;
        var result = SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl);
        Assert.Equal(108.12345678, result.Points[2].Existing);
        Assert.All(result.Points.Skip(4).Take(5), p => Assert.Equal(100, p.Existing));
        Assert.Equal(99.45678, result.Points[3].Design);
        Assert.Equal(6, result.ProjectAnchorIndex); Assert.Equal(12, result.CenterOffset);
    }

    [Fact]
    public void SavedSurveyUndulationsAreNotSilentlyReplacedWithIdealBowl()
    {
        var source = SectionApproximation.DistributeDistances(Draft(13, 6), 24);
        source.Points[2].Existing = 106; source.Points[4].Existing = 107;
        var result = SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl);
        Assert.InRange(result.Points[1].Existing.Value, 106, 110);
        Assert.InRange(result.Points[3].Existing.Value, 106, 107);
        Assert.InRange(result.Points[5].Existing.Value, 100, 107);
    }

    [Fact]
    public void WidthConflictsAreAtomicAndExplicitRedistributionWorks()
    {
        var source = Draft();
        source.Points[8].DistanceFromPrevious = 15;
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.DistributeDistances(source, 20));
        Assert.Null(source.Points[1].DistanceFromPrevious);
        Assert.Equal(15, source.Points[8].DistanceFromPrevious);
        var replaced = SectionApproximation.DistributeDistances(source, 20, preserveEntered: false);
        Assert.All(replaced.Points.Skip(1), p => Assert.Equal(2.5, p.DistanceFromPrevious));
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.DistributeDistances(replaced, 30));
        var resized = SectionApproximation.DistributeDistances(replaced, 30, preserveEntered: false);
        Assert.Equal(30, resized.Points.Last().Offset, 10);
    }

    [Theory]
    [InlineData(0, null)] [InlineData(-1, null)] [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)] [InlineData(20, 0d)] [InlineData(20, 20d)]
    [InlineData(20, -1d)] [InlineData(20, double.NaN)]
    public void RejectsInvalidWidthsAndBottomPositions(double width, double? bottom)
    {
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.DistributeDistances(Draft(), width, bottom));
    }

    [Fact]
    public void RequiresUsableRoleOrderAnchorsAndDistances()
    {
        var source = Draft();
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl));
        source.Points[1].Role = SectionPointRole.Bottom;
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.DistributeDistances(source, 20));
        source.Points[1].Role = SectionPointRole.Regular;
        source.Points[0].Role = SectionPointRole.RightBank; source.Points[8].Role = SectionPointRole.LeftBank;
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.DistributeDistances(source, 20));
        source = SectionApproximation.DistributeDistances(Draft(), 20);
        source.Points[0].Existing = 99;
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl));
        source.Points[0].Existing = null;
        Assert.Throws<ProfileValidationException>(() => SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl));
    }

    [Fact]
    public void ExteriorPointsGetSideStepAndLinearExtrapolation()
    {
        var source = Draft(5, 2);
        source.Points.Insert(0, new SectionPoint { Offset = -5, DistanceFromPrevious = 0 });
        source.Points[1].DistanceFromPrevious = null;
        source.Points.Add(new SectionPoint());
        var spaced = SectionApproximation.DistributeDistances(source, 20);
        Assert.Equal(20, spaced.Points[5].Offset - spaced.Points[1].Offset, 10);
        Assert.Equal(5, spaced.Points[1].DistanceFromPrevious);
        Assert.Equal(5, spaced.Points[6].DistanceFromPrevious);
        var filled = SectionApproximation.FillElevations(spaced, SectionApproximationShape.StraightSlopes);
        Assert.Equal(115, filled.Points[0].Existing);
        Assert.Equal(118, filled.Points[6].Existing);
    }

    [Fact]
    public void LatestLinkedAnchorsAreReadButNotWrittenBackToLongitudinal()
    {
        var source = SectionApproximation.DistributeDistances(Draft(), 20);
        source.Station = 25;
        foreach (var p in source.Points.Where(p => p.Role != SectionPointRole.Regular)) p.UseLongitudinalElevation = true;
        var longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, LeftBank = 120, ExistingBottom = 110, RightBank = 122 },
            new() { Station = 50, LeftBank = 122, ExistingBottom = 112, RightBank = 124 }
        };
        var filled = SectionApproximation.FillElevations(source, SectionApproximationShape.SmoothBowl, longitudinal);
        Assert.Equal(111, filled.Points[4].Existing);
        Assert.Equal(121, filled.Points[0].Existing);
        Assert.Equal(123, filled.Points[8].Existing);
        Assert.Equal(100, source.Points[4].Existing);
        Assert.Equal(2, longitudinal.Count);
        Assert.True(filled.Points[4].UseLongitudinalElevation);
    }

    [Fact]
    public void CompleteProfileRoundTripsAndBuildsRealGeometry()
    {
        var section = SectionApproximation.FillElevations(SectionApproximation.DistributeDistances(Draft(), 20), SectionApproximationShape.SmoothBowl);
        var project = new ProfileProject { Kind = ProfileKind.CrossSection, Sections = new() { section } };
        var loaded = ProjectFile.Deserialize(ProjectFile.Serialize(project));
        Assert.Equal(section.Points.Select(p => p.Existing), loaded.Sections[0].Points.Select(p => p.Existing));
        var plan = ProfileEngine.Build(loaded);
        Assert.NotEmpty(plan.Entities.OfType<DrawPolyline>());
        Assert.Contains(plan.Entities.OfType<DrawPolyline>(), line => line.Points.Count >= 9);
    }

    [Fact]
    public void TenThousandPointsStayStrictlyOrderedAndBounded()
    {
        var section = SectionApproximation.DistributeDistances(Draft(10000, 4123), 111.12345678, 40.12345678);
        var filled = SectionApproximation.FillElevations(section, SectionApproximationShape.SmoothBowl);
        Assert.Equal(111.12345678, filled.Points.Last().Offset, 7);
        Assert.All(filled.Points, p => Assert.InRange(p.Existing.Value, 100, 112));
        for (int i = 1; i < filled.Points.Count; i++) Assert.True(filled.Points[i].Offset > filled.Points[i - 1].Offset);
    }
}
