using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class DrawingSurveyTests
{
    [Fact]
    public void LongitudinalChainUsesCumulativePlanarDistancesAndUnroundedMetreElevations()
    {
        var picks = new List<LongitudinalSurveyPick>
        {
            new() { Position = new Point2(1000, 2000), Values = new LongitudinalPoint { Station = 99, Ground = 123.123456789 } },
            new() { Position = new Point2(1003, 2004), Values = new LongitudinalPoint { Water = 0 } },
            new() { Position = new Point2(1006, 2004) }
        };
        var rows = DrawingSurvey.BuildLongitudinal(picks, 650, SurveySourceUnits.ModelMetres());
        Assert.Equal(new[] { 650.0, 655, 658 }, rows.Select(row => row.Station));
        Assert.Equal(123.123456789, rows[0].Ground);
        Assert.Equal(0, rows[1].Water);
        Assert.Null(rows[1].Ground);
        Assert.Null(rows[2].ProjectBottom);
        rows[0].Ground = 1;
        Assert.Equal(123.123456789, picks[0].Values.Ground);
        Assert.Equal(99, picks[0].Values.Station);
    }

    [Fact]
    public void SourceUnitModesConvertDistancesWithoutScalingElevationValues()
    {
        var picks = new List<SectionSurveyPick>
        {
            new() { Position = new Point2(0, 0), Values = new SectionPoint { Existing = 123.123456789 } },
            new() { Position = new Point2(3, 4), Values = new SectionPoint { Existing = 124.123456789 } }
        };
        Assert.Equal(5, DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres()).Points[1].Offset);
        Assert.Equal(5, DrawingSurvey.BuildSection(picks, SurveySourceUnits.PaperMillimetres(1000)).Points[1].Offset);
        Assert.Equal(2.5, DrawingSurvey.BuildSection(picks, SurveySourceUnits.PaperMillimetres(500)).Points[1].Offset);
        Assert.Equal(.005, DrawingSurvey.BuildSection(picks, SurveySourceUnits.Custom(.001)).Points[1].Offset, 12);
        var calibrated = SurveySourceUnits.Calibrated(new Point2(0, 0), new Point2(40, 30), 125);
        var result = DrawingSurvey.BuildSection(picks, calibrated);
        Assert.Equal(12.5, result.Points[1].DistanceFromPrevious);
        Assert.Equal(123.123456789, result.Points[0].Existing);
    }

    [Fact]
    public void CrossSurveyInfersUnambiguousBankAndLowestInteriorRolesWithoutProjectValues()
    {
        var picks = Picks(12, 9, 10, 13);
        var section = DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres());
        Assert.Equal(new[] { 0.0, 5, 10, 15 }, section.Points.Select(point => point.Offset));
        Assert.Equal(new double?[] { 0, 5, 5, 5 }, section.Points.Select(point => point.DistanceFromPrevious));
        Assert.Equal(new[] { SectionPointRole.LeftBank, SectionPointRole.Bottom, SectionPointRole.Regular, SectionPointRole.RightBank }, section.Points.Select(point => point.Role));
        Assert.Equal(12, section.LeftBank);
        Assert.Equal(9, section.ExistingBottom);
        Assert.Equal(13, section.RightBank);
        Assert.Equal(SectionDesignMode.None, section.DesignMode);
        Assert.Null(section.Station);
        Assert.Null(section.ProjectBottom);
        Assert.Null(section.BottomWidth);
        Assert.Null(section.Water);
        Assert.All(section.Points, point => Assert.Null(point.Design));
        Assert.True(Guid.TryParseExact(section.SectionId, "N", out _));
        Assert.All(picks, pick => Assert.Equal(SectionPointRole.Regular, pick.Values.Role));
    }

    [Fact]
    public void ExplicitRolesAndLabelsArePreservedRatherThanOverriddenByInference()
    {
        var picks = Picks(12, 9, 13);
        picks[1].Values.Role = SectionPointRole.LeftBank;
        picks[1].Values.Label = "Кромка камыша";
        picks[2].Values.Design = 11.123456789;
        var section = DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres());
        Assert.Equal(new[] { SectionPointRole.Regular, SectionPointRole.LeftBank, SectionPointRole.Regular }, section.Points.Select(point => point.Role));
        Assert.Equal("Кромка камыша", section.Points[1].Label);
        Assert.Equal(9, section.LeftBank);
        Assert.Null(section.RightBank);
        Assert.Null(section.ExistingBottom);
        Assert.Equal(SectionDesignMode.Points, section.DesignMode);
        Assert.Equal(11.123456789, section.Points[2].Design);
    }

    [Fact]
    public void InvalidCaptureNeverChangesSourceBuffersOrReturnsPartialImport()
    {
        var picks = Picks(12, 9, 13);
        picks[2].Position = picks[1].Position;
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres()));
        Assert.All(picks, pick => Assert.Equal(0, pick.Values.Offset));
        Assert.Throws<ProfileValidationException>(() => SurveySourceUnits.Custom(0));
        Assert.Throws<ProfileValidationException>(() => SurveySourceUnits.Calibrated(new Point2(0, 0), new Point2(0, 0), 100));
        picks[2].Position = new Point2(double.NaN, 0);
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres()));
        picks = Picks(12, 9, 13);
        picks[0].Values.Role = picks[1].Values.Role = SectionPointRole.LeftBank;
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.BuildSection(picks, SurveySourceUnits.ModelMetres()));
    }

    [Fact]
    public void PositiveStepsTooSmallToAdvanceAStoredStationOrOffsetAreRejected()
    {
        var longitudinal = new List<LongitudinalSurveyPick>
        {
            new() { Position = new Point2(0, 0) }, new() { Position = new Point2(1e-12, 0) }
        };
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.BuildLongitudinal(longitudinal, 1e9, SurveySourceUnits.ModelMetres()));
        var cross = new List<SectionSurveyPick>
        {
            new() { Position = new Point2(0, 0) }, new() { Position = new Point2(1e9, 0) },
            new() { Position = new Point2(1e9, 1e-12) }
        };
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.BuildSection(cross, SurveySourceUnits.ModelMetres()));
    }

    [Fact]
    public void MergeOverlaysOnlyEnteredFieldsAtSortedStationsAndPreservesOtherProfiles()
    {
        var source = new ProfileProject();
        source.Longitudinal.Add(new LongitudinalPoint { Station = 650, Ground = 88, Water = 77 });
        string inactive = source.ProfileId;
        source = ProfileCollection.Add(source, "Второй канал");
        source.Longitudinal.Add(new LongitudinalPoint { Station = 700, Ground = 17 });
        source.Longitudinal.Add(new LongitudinalPoint { Station = 650, Ground = 10, Water = 5, ProjectBottom = 7, PointName = "Старая точка" });
        string original = ProjectFile.Serialize(source);
        var imported = new List<LongitudinalPoint>
        {
            new() { Station = 650, Ground = 10.123456789, Water = 0, PointName = "" },
            new() { Station = 600, ExistingBottom = 8 }
        };
        var result = DrawingSurvey.MergeLongitudinal(source, imported);
        Assert.Equal(new[] { 600.0, 650, 700 }, result.Longitudinal.Select(point => point.Station));
        Assert.Equal(10.123456789, result.Longitudinal[1].Ground);
        Assert.Equal(0, result.Longitudinal[1].Water);
        Assert.Equal(7, result.Longitudinal[1].ProjectBottom);
        Assert.Equal("Старая точка", result.Longitudinal[1].PointName);
        Assert.Equal(88, ProfileCollection.Select(result, inactive).Longitudinal[0].Ground);
        Assert.Equal(77, ProfileCollection.Select(result, inactive).Longitudinal[0].Water);
        Assert.Equal(original, ProjectFile.Serialize(source));
        Assert.Null(imported[0].ProjectBottom);
    }

    [Fact]
    public void IdenticalEnteredCommonValueStillReplacesAnOldUnlinkedCrossValue()
    {
        var source = new ProfileProject();
        source.Longitudinal.Add(new LongitudinalPoint { Station = 0, Water = 10 });
        source.Sections.Add(new CrossSection { Station = 0, Water = 99, UseLongitudinalWater = false });
        var result = DrawingSurvey.MergeLongitudinal(source, new List<LongitudinalPoint> { new() { Station = 0, Water = 10 } });
        Assert.Equal(10, result.Sections[0].Water);
        Assert.True(result.Sections[0].UseLongitudinalWater);
        Assert.Equal(99, source.Sections[0].Water);
    }

    [Fact]
    public void DuplicateImportedStationFailsAtomically()
    {
        var source = new ProfileProject();
        source.Longitudinal.Add(new LongitudinalPoint { Station = 0, Ground = 10 });
        string original = ProjectFile.Serialize(source);
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.MergeLongitudinal(source, new List<LongitudinalPoint>
        {
            new() { Station = 0, Ground = 12 }, new() { Station = 0, Ground = 13 }
        }));
        Assert.Equal(original, ProjectFile.Serialize(source));
    }

    [Fact]
    public void CapturedUnsitedSurveyRetainsMeasurementsUntilStationAssignmentThenWinsSharedValues()
    {
        var source = new ProfileProject();
        source.Longitudinal.Add(new LongitudinalPoint
        {
            Station = 650, LeftBank = 100, ExistingBottom = 99, RightBank = 200,
            Water = 10, ProjectBottom = 8, BottomWidth = 2, LeftSlope = 1.5, RightSlope = 2
        });
        var captured = DrawingSurvey.BuildSection(Picks(11, 9, 12), SurveySourceUnits.ModelMetres());
        var imported = DrawingSurvey.AppendSections(source, new[] { captured });
        Assert.Empty(source.Sections);
        Assert.Equal(captured.SectionId, imported.Sections[0].SectionId);
        Assert.Null(imported.Sections[0].Station);
        Assert.Equal(100, imported.Longitudinal[0].LeftBank);
        var before = ProfileInterpolation.CloneSection(imported.Sections[0]);
        var assigned = ProfileInterpolation.CloneSection(before);
        assigned.Station = 650;
        var result = ProjectSynchronization.CommitSection(imported, 0, before, assigned);
        Assert.Equal("ПК 6+50.00", result.Sections[0].Name);
        Assert.Equal(11, result.Longitudinal[0].LeftBank);
        Assert.Equal(9, result.Longitudinal[0].ExistingBottom);
        Assert.Equal(12, result.Longitudinal[0].RightBank);
        Assert.Equal(10, result.Sections[0].Water);
        Assert.Equal(8, result.Sections[0].ProjectBottom);
        Assert.Equal(2, result.Sections[0].BottomWidth);
        Assert.Equal(1.5, result.Sections[0].LeftSlope);
        Assert.Equal(2, result.Sections[0].RightSlope);
        Assert.Equal(new[] { 11.0, 9, 12 }, result.Sections[0].Points.Select(point => point.Existing!.Value));
        Assert.Equal(new double?[] { 0, 5, 5 }, result.Sections[0].Points.Select(point => point.DistanceFromPrevious));
        Assert.All(result.Sections[0].Points, point => Assert.True(point.UseLongitudinalElevation));
        Assert.Null(captured.Station);
    }

    [Fact]
    public void ExplicitManualStationAssignmentCreatesBlankStationRowEvenWithoutElevations()
    {
        var source = new ProfileProject();
        source.Longitudinal.Add(new LongitudinalPoint { Station = 600 });
        source.Longitudinal.Add(new LongitudinalPoint { Station = 700 });
        source.Sections.Add(new CrossSection());
        var before = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Station = 650;
        var result = ProjectSynchronization.CommitSection(source, 0, before, edited);
        Assert.Equal(new[] { 600.0, 650, 700 }, result.Longitudinal.Select(point => point.Station));
        Assert.Null(result.Longitudinal[1].ExistingBottom);
        Assert.Null(result.Longitudinal[1].ProjectBottom);
        Assert.Equal("ПК 6+50.00", result.Sections[0].Name);
        Assert.Equal(2, source.Longitudinal.Count);
    }

    [Fact]
    public void AppendWithKnownStationCreatesOnlyActiveProfileStationAndUsesNewSectionIdentity()
    {
        var source = new ProfileProject();
        string firstProfile = source.ProfileId;
        source = ProfileCollection.Add(source, "Второй");
        var captured = DrawingSurvey.BuildSection(Picks(11, 9, 12), SurveySourceUnits.ModelMetres(), 650);
        var result = DrawingSurvey.AppendSections(source, new[] { captured });
        Assert.Single(result.Longitudinal);
        Assert.Equal(650, result.Longitudinal[0].Station);
        Assert.Equal(9, result.Longitudinal[0].ExistingBottom);
        Assert.Empty(ProfileCollection.Select(result, firstProfile).Sections);
        Assert.Empty(ProfileCollection.Select(result, firstProfile).Longitudinal);
        Assert.Empty(source.Sections);
        Assert.Throws<ProfileValidationException>(() => DrawingSurvey.AppendSections(result, new[] { captured }));
    }

    private static List<SectionSurveyPick> Picks(params double?[] heights) => heights.Select((height, index) => new SectionSurveyPick
    {
        Position = new Point2(index * 3, index * 4),
        Values = new SectionPoint { Existing = height }
    }).ToList();
}
