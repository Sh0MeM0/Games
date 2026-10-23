using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProjectSynchronizationTests
{
    [Fact]
    public void AllSharedDimensionsAndElevationsTransferWithoutTouchingLocalSurvey()
    {
        var source = Project();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.LeftBank = 20.123456789;
        edited.RightBank = 21;
        edited.ExistingBottom = 12;
        edited.Water = 15;
        edited.ProjectBottom = 11;
        edited.BottomWidth = 3.25;
        edited.LeftSlope = 1.75;
        edited.RightSlope = 2.25;
        edited.Points.Add(new SectionPoint { Role = SectionPointRole.Regular, Existing = 17, Offset = 5.55 });
        edited.SiltThickness = .4;

        var actual = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        var station = actual.Longitudinal[0];
        Assert.Equal(20.123456789, station.LeftBank);
        Assert.Equal(21, station.RightBank);
        Assert.Equal(12, station.ExistingBottom);
        Assert.Equal(15, station.Water);
        Assert.Equal(11, station.ProjectBottom);
        Assert.Equal(3.25, station.BottomWidth);
        Assert.Equal(1.75, station.LeftSlope);
        Assert.Equal(2.25, station.RightSlope);
        Assert.Equal(20.123456789, actual.Sections[0].Points[0].Existing);
        Assert.Equal(12, actual.Sections[0].Points[1].Existing);
        Assert.Equal(21, actual.Sections[0].Points[2].Existing);
        Assert.Equal(17, actual.Sections[0].Points[3].Existing);
        Assert.Equal(5.55, actual.Sections[0].Points[3].Offset);
        Assert.Equal(.4, actual.Sections[0].SiltThickness);
        Assert.Null(actual.Sections[0].Points[1].DistanceFromPrevious);
        Assert.True(actual.Sections[0].UseLongitudinalProjectBottom);
        Assert.True(actual.Sections[0].UseLongitudinalWater);
        Assert.All(actual.Sections[0].Points.Take(3), point => Assert.True(point.UseLongitudinalElevation));
    }

    [Fact]
    public void RoleCellEditWritesThroughAndLaterLongitudinalEditStaysLive()
    {
        var source = Project();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Points[1].Existing = 7.123456789;
        edited.Points[1].UseLongitudinalElevation = false; // Previous editor versions cleared this flag on manual entry.
        var committed = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        Assert.Equal(7.123456789, committed.Longitudinal[0].ExistingBottom);
        Assert.Equal(7.123456789, committed.Sections[0].ExistingBottom);
        Assert.True(committed.Sections[0].Points[1].UseLongitudinalElevation);
        var refreshed = ProjectSynchronization.SetSharedValue(committed, 0, SharedProfileField.ExistingBottom, 8.987654321);
        Assert.Equal(8.987654321, refreshed.Sections[0].Points[1].Existing);
    }

    [Fact]
    public void ConflictingScalarAndRoleEditsFailAtomically()
    {
        var source = Project();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.LeftBank = 20;
        edited.Points[0].Existing = 21;
        Assert.Throws<ProfileValidationException>(() => ProjectSynchronization.CommitSection(source, 0, previous, edited));
        Assert.Equal(10, source.Longitudinal[0].LeftBank);
        Assert.Equal(10, source.Sections[0].Points[0].Existing);
    }

    [Fact]
    public void AmbiguousDuplicateRolesCannotSilentlyOverwriteSurvey()
    {
        var source = Project();
        source.Sections[0].Points.Add(new SectionPoint { Role = SectionPointRole.LeftBank, Existing = 20 });
        Assert.Throws<ProfileValidationException>(() => ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.LeftBank, 15));
        Assert.Equal(10, source.Sections[0].Points[0].Existing);
        Assert.Equal(20, source.Sections[0].Points[3].Existing);
        // An unrelated water edit is safe and must not reject or collapse the ambiguous bank geometry.
        var water = ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.Water, 9);
        Assert.Equal(10, water.Sections[0].Points[0].Existing);
        Assert.Equal(20, water.Sections[0].Points[3].Existing);
    }

    [Fact]
    public void SameStationCrossSectionsReceiveOnlyChangedSharedField()
    {
        var source = Project();
        var second = ProfileInterpolation.CloneSection(source.Sections[0]);
        second.SectionId = Guid.NewGuid().ToString("N");
        second.Name = "Другой промер";
        second.Points[1].DistanceFromPrevious = 12.3456789;
        second.SiltThickness = .75;
        source.Sections.Add(second);
        var result = ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.RightBank, 22);
        Assert.Equal(new[] { "ПК 0+00.00", "Другой промер" }, result.Sections.Select(section => section.Name));
        Assert.All(result.Sections, section => Assert.Equal(22, section.Points[2].Existing));
        Assert.Null(result.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Equal(12.3456789, result.Sections[1].Points[1].DistanceFromPrevious);
        Assert.Equal(.75, result.Sections[1].SiltThickness);
    }

    [Fact]
    public void DuplicateLongitudinalStationsRejectWithoutSelectingOneArbitrarily()
    {
        var source = Project();
        source.Longitudinal.Add(new LongitudinalPoint { Station = 0, Water = 99 });
        Assert.Throws<ProfileValidationException>(() => ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.Water, 8));
        Assert.Equal(2, source.Longitudinal.Count);
        Assert.Equal(99, source.Longitudinal[1].Water);
    }

    [Fact]
    public void NumericStationSortingKeepsBareNumbersInMetres()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = NumberParser.ParseRequired("7+00", "ПК"), PointName = "700" },
            new() { Station = NumberParser.ParseRequired("6+50", "ПК"), PointName = "650" },
            new() { Station = NumberParser.ParseRequired("6", "ПК"), PointName = "6" }
        };
        var sorted = ProjectSynchronization.SortLongitudinal(source);
        Assert.Equal(new[] { 6.0, 650, 700 }, sorted.Select(point => point.Station));
        Assert.Equal(new[] { "6", "650", "700" }, sorted.Select(point => point.PointName));
        Assert.Equal(700, source[0].Station);
        Assert.NotSame(source[0], sorted[2]);
    }

    [Fact]
    public void NoOpCommitDoesNotCreateLongitudinalRowsForLegacyCrossSection()
    {
        var source = Project();
        source.Longitudinal.Clear();
        var section = source.Sections[0];
        section.UseLongitudinalProjectBottom = section.UseLongitudinalWater = false;
        foreach (var point in section.Points) point.UseLongitudinalElevation = false;
        var snapshot = ProfileInterpolation.CloneSection(section);
        var result = ProjectSynchronization.CommitSection(source, 0, snapshot, snapshot);
        Assert.Empty(result.Longitudinal);
        Assert.Equal(source.ProjectId, result.ProjectId);
        Assert.Equal(source.Name, result.Name);
        Assert.Equal(section.Points[0].Existing, result.Sections[0].Points[0].Existing);
    }

    [Fact]
    public void SafeRefreshPreservesUnlinkedLegacySharedValues()
    {
        var source = Project();
        source.Sections[0].Water = 99;
        source.Sections[0].UseLongitudinalWater = false;
        source.Sections[0].BottomWidth = 12;
        source.Sections[0].Points[0].Existing = 88;
        source.Sections[0].Points[0].UseLongitudinalElevation = false;
        source.Longitudinal[0].ExistingBottom = 8;
        var result = ProjectSynchronization.RefreshFromLongitudinal(source);
        Assert.Equal(99, result.Sections[0].Water);
        Assert.Equal(12, result.Sections[0].BottomWidth);
        Assert.Equal(88, result.Sections[0].Points[0].Existing);
        Assert.Equal(8, result.Sections[0].Points[1].Existing);
    }

    [Fact]
    public void SelectedManualDesignAnchorSynchronizesWithoutChangingOtherDesignCells()
    {
        var source = Project();
        var section = source.Sections[0];
        section.DesignMode = SectionDesignMode.Points;
        section.Points[0].Design = 10;
        section.Points[1].Design = 5;
        section.Points[2].Design = 12;
        var before = ProfileInterpolation.CloneSection(section);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Points[1].Design = 4.123456789;
        var result = ProjectSynchronization.CommitSection(source, 0, before, edited);
        Assert.Equal(4.123456789, result.Longitudinal[0].ProjectBottom);
        var back = ProjectSynchronization.SetSharedValue(result, 0, SharedProfileField.ProjectBottom, 3.987654321);
        Assert.Equal(3.987654321, back.Sections[0].Points[1].Design);
        Assert.Equal(10, back.Sections[0].Points[0].Design);
        Assert.Equal(12, back.Sections[0].Points[2].Design);
        Assert.Equal(5, source.Sections[0].Points[1].Design);
    }

    [Fact]
    public void LiveManualDesignAnchorResolvesBeforeDesignInterpolation()
    {
        var source = Project();
        var section = source.Sections[0];
        section.DesignMode = SectionDesignMode.Points;
        section.Points[0].Design = 10;
        section.Points[1].Design = 5;
        section.Points[2].Design = 12;
        section.Points[1].DistanceFromPrevious = 2;
        section.Points[2].DistanceFromPrevious = 3;
        source.Longitudinal[0].ProjectBottom = 4;
        var resolved = ProfileInterpolation.ResolveSection(section, source.Longitudinal);
        Assert.Equal(4, resolved.Points[1].Design);
        Assert.Equal(5, section.Points[1].Design);
    }

    [Fact]
    public void UnassignedSharedEditStaysInDraftUntilStationIsAssigned()
    {
        var source = Project();
        source.Sections[0].Station = null;
        var before = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Points[0].Existing = 30.123456789;
        var result = ProjectSynchronization.CommitSection(source, 0, before, edited);
        Assert.Equal(30.123456789, result.Sections[0].LeftBank);
        Assert.False(result.Sections[0].Points[0].UseLongitudinalElevation);
        Assert.Equal(10, result.Longitudinal[0].LeftBank);
        Assert.Single(result.Longitudinal);
    }

    private static ProfileProject Project()
    {
        var source = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint>
            {
                new() { Station = 0, LeftBank = 10, RightBank = 12, ExistingBottom = 6,
                    Water = 7, ProjectBottom = 5, BottomWidth = 2, LeftSlope = 1, RightSlope = 1 }
            }
        };
        source.Sections.Add(ProfileInterpolation.CreateSection(source.Longitudinal, 0));
        return source;
    }
}
