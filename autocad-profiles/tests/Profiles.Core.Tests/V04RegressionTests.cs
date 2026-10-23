using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

/// <summary>Independent regressions for editor transactions and display-only geometry.</summary>
public sealed class V04RegressionTests
{
    [Fact]
    public void EditingOneFieldFromOlderSectionSnapshotPreservesNewerMasterFields()
    {
        var source = LinkedProject();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var current = ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.LeftBank, 12);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Water = 8;
        edited.Points[1].DistanceFromPrevious = 3.125;
        edited.Points[2].DistanceFromPrevious = 4.75;
        edited.SiltThickness = .25;

        var committed = ProjectSynchronization.CommitSection(current, 0, previous, edited);

        Assert.Equal(12, committed.Longitudinal[0].LeftBank);
        Assert.Equal(12, committed.Sections[0].LeftBank);
        Assert.Equal(12, committed.Sections[0].Points[0].Existing);
        Assert.Equal(8, committed.Longitudinal[0].Water);
        Assert.Equal(8, committed.Sections[0].Water);
        Assert.Equal(3.125, committed.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Equal(4.75, committed.Sections[0].Points[2].DistanceFromPrevious);
        Assert.Equal(.25, committed.Sections[0].SiltThickness);
        Assert.Equal(10, previous.LeftBank);
        Assert.Equal(10, edited.LeftBank);
        Assert.Equal(7, current.Longitudinal[0].Water);
    }

    [Fact]
    public void DeliberateNewEditOfSameFieldWinsOverEarlierMasterChange()
    {
        var source = LinkedProject();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var current = ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.ProjectBottom, 6);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.ProjectBottom = 4.123456789;

        var committed = ProjectSynchronization.CommitSection(current, 0, previous, edited);

        Assert.Equal(4.123456789, committed.Longitudinal[0].ProjectBottom);
        Assert.Equal(4.123456789, committed.Sections[0].ProjectBottom);
        Assert.Equal(6, current.Longitudinal[0].ProjectBottom);
    }

    [Fact]
    public void ClearingRawAnchorAllowsReinterpolationWithoutSavingDerivedValueAsAnchor()
    {
        var source = MiddleStationProject(99);
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.ProjectBottom = null;

        var committed = ProjectSynchronization.CommitSection(source, 0, previous, edited);

        Assert.Null(committed.Longitudinal[1].ProjectBottom);
        Assert.Equal(15, ProfileInterpolation.SampleLongitudinal(committed.Longitudinal, 50).ProjectBottom);
        Assert.Equal(15, committed.Sections[0].ProjectBottom);
        var reopened = ProjectFile.Clone(committed);
        Assert.Null(reopened.Longitudinal[1].ProjectBottom);
        Assert.Equal(99, source.Longitudinal[1].ProjectBottom);
    }

    [Fact]
    public void ChangingWaterDoesNotPromoteDisplayedInterpolatedProjectBottomToRawAnchor()
    {
        var source = MiddleStationProject(null);
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        Assert.Equal(15, previous.ProjectBottom);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Water = 13.123456789;

        var committed = ProjectSynchronization.CommitSection(source, 0, previous, edited);

        Assert.Null(committed.Longitudinal[1].ProjectBottom);
        Assert.Equal(13.123456789, committed.Longitudinal[1].Water);
        var changedEndpoint = ProjectSynchronization.SetSharedValue(committed, 100, SharedProfileField.ProjectBottom, 30);
        Assert.Equal(20, changedEndpoint.Sections[0].ProjectBottom);
        Assert.Null(changedEndpoint.Longitudinal[1].ProjectBottom);
    }

    [Fact]
    public void ManualProjectBottomCanBecomeLatestValueFromEitherEditor()
    {
        var source = LinkedProject();
        source.Sections[0].UseLongitudinalProjectBottom = false;
        source.Sections[0].ProjectBottom = 100;
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.ProjectBottom = 101.123456789;
        var fromCross = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        Assert.Equal(101.123456789, fromCross.Longitudinal[0].ProjectBottom);

        var fromLong = ProjectSynchronization.SetSharedValue(fromCross, 0, SharedProfileField.ProjectBottom, 102.987654321);
        Assert.Equal(102.987654321, fromLong.Sections[0].ProjectBottom);
        Assert.Equal(102.987654321, fromLong.Longitudinal[0].ProjectBottom);
        Assert.Equal(100, source.Sections[0].ProjectBottom);
        Assert.Null(fromLong.Sections[0].Points[1].DistanceFromPrevious);
    }

    [Fact]
    public void LongitudinalWaterChangePreservesUnrelatedLegacyManualProjectBottom()
    {
        var source = LinkedProject();
        source.Sections[0].UseLongitudinalProjectBottom = false;
        source.Sections[0].ProjectBottom = 100;
        source.Sections[0].Points.Add(new SectionPoint { Existing = 77, Role = SectionPointRole.Regular });
        var previous = ProjectFile.Clone(source).Longitudinal;
        source.Longitudinal[0].Water = 8;

        var refreshed = ProjectSynchronization.RefreshFromLongitudinal(source, previous);

        Assert.Equal(8, refreshed.Sections[0].Water);
        Assert.Equal(100, refreshed.Sections[0].ProjectBottom);
        Assert.Equal(77, refreshed.Sections[0].Points[3].Existing);
        Assert.Equal(5, refreshed.Longitudinal[0].ProjectBottom);
    }

    [Fact]
    public void SynchronizationResultsDoNotAliasSourceOrEditBuffers()
    {
        var source = LinkedProject();
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Water = 8;
        var committed = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        committed.Settings.Title = "Changed only in returned copy";
        committed.Longitudinal[0].Ground = 999;
        committed.Sections[0].Points[1].DistanceFromPrevious = 3.123456789;
        committed.Sections[0].Points[0].Existing = 88;

        Assert.NotSame(source, committed);
        Assert.NotSame(source.Settings, committed.Settings);
        Assert.NotSame(source.Longitudinal[0], committed.Longitudinal[0]);
        Assert.NotSame(edited.Points[0], committed.Sections[0].Points[0]);
        Assert.Null(source.Longitudinal[0].Ground);
        Assert.Null(source.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Null(edited.Points[1].DistanceFromPrevious);
        Assert.Equal(10, edited.Points[0].Existing);
        Assert.Equal(10, source.Sections[0].Points[0].Existing);
        Assert.NotEqual(committed.Settings.Title, source.Settings.Title);
    }

    [Fact]
    public void AssigningUnsitedDraftInsertsNumericPicketAndPreservesUnknownDistances()
    {
        var source = LinkedProject();
        var draft = source.Sections[0];
        draft.Station = null;
        draft.UseLongitudinalProjectBottom = draft.UseLongitudinalWater = false;
        foreach (var point in draft.Points) point.UseLongitudinalElevation = false;
        draft.ProjectBottom = 3.141592653589793;
        source.Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 600, LeftBank = 10, Water = 6 },
            new() { Station = 700, LeftBank = 20, Water = 8 }
        };
        var previous = ProfileInterpolation.CloneSection(draft);
        var assigned = ProfileInterpolation.CloneSection(draft);
        assigned.Station = NumberParser.ParseRequired("6+50", "Пикет");

        var committed = ProjectSynchronization.CommitSection(source, 0, previous, assigned);

        Assert.Equal(new[] { 600.0, 650, 700 }, committed.Longitudinal.Select(point => point.Station));
        Assert.Equal(3.141592653589793, committed.Longitudinal[1].ProjectBottom);
        Assert.Equal(10, committed.Longitudinal[1].LeftBank);
        Assert.Equal(650, committed.Sections[0].Station);
        Assert.Null(committed.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Null(committed.Sections[0].Points[2].DistanceFromPrevious);
        Assert.Null(source.Sections[0].Station);
        Assert.Equal(2, source.Longitudinal.Count);
    }

    [Fact]
    public void SamePicketSectionsShareExplicitEditWithoutMergingTheirLocalGeometry()
    {
        var source = LinkedProject();
        var other = ProfileInterpolation.CloneSection(source.Sections[0]);
        other.SectionId = Guid.NewGuid().ToString("N");
        other.Name = "Вторая съёмка";
        other.CenterOffset = 17;
        other.SiltThickness = .123456789;
        other.Points[1].DistanceFromPrevious = 9.87654321;
        other.Points[2].DistanceFromPrevious = 11.123456789;
        source.Sections[0].Name = "Первая съёмка";
        source.Sections.Add(other);

        var changed = ProjectSynchronization.SetSharedValue(source, 0, SharedProfileField.ProjectBottom, 4.0123456789);

        Assert.Equal(new[] { "Первая съёмка", "Вторая съёмка" }, changed.Sections.Select(section => section.Name));
        Assert.All(changed.Sections, section => Assert.Equal(4.0123456789, section.ProjectBottom));
        Assert.Null(changed.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Equal(9.87654321, changed.Sections[1].Points[1].DistanceFromPrevious);
        Assert.Equal(11.123456789, changed.Sections[1].Points[2].DistanceFromPrevious);
        Assert.Equal(17, changed.Sections[1].CenterOffset);
        Assert.Equal(.123456789, changed.Sections[1].SiltThickness);
        Assert.Equal(5, source.Sections[1].ProjectBottom);
        var volumes = Earthworks.CalculateVolumes(changed);
        Assert.Null(volumes.TotalCutVolume);
        Assert.Contains(volumes.Warnings, warning => warning.Contains("один пикет"));
    }

    [Fact]
    public void DisplayAddsAnalyticIntersectionWithoutChangingSurveyOrRawDesign()
    {
        var raw = Section(new() { Offset = 0, Existing = 0, Design = 4 },
            new() { Offset = 10, Existing = 10, Design = 0 });
        var calculation = Earthworks.Calculate(raw);
        var displayed = SectionDisplayGeometry.Build(calculation);
        var intersection = Assert.Single(displayed, point => Math.Abs(point.Offset - 20.0 / 7) < 1e-12);

        Assert.Equal(20.0 / 7, intersection.Existing!.Value, 11);
        Assert.Equal(intersection.Existing.Value, intersection.Design!.Value, 11);
        Assert.Equal(3, displayed.Count);
        Assert.Equal(2, raw.Points.Count);
        Assert.Equal(2, calculation.ResolvedSection.Points.Count);
        displayed[0].Existing = 500;
        Assert.Equal(0, raw.Points[0].Existing);
        Assert.Equal(0, calculation.ResolvedSection.Points[0].Existing);
    }

    [Fact]
    public void DisplayDoesNotInventIntersectionAcrossMissingSurveyOrFillRawGap()
    {
        var raw = Section(new() { Offset = 0, Existing = 0, Design = 4 },
            new() { Offset = 2, Existing = 2 }, new() { Offset = 4 },
            new() { Offset = 6, Existing = 6 }, new() { Offset = 10, Existing = 10, Design = 0 });

        var displayed = SectionDisplayGeometry.Build(Earthworks.Calculate(raw));

        Assert.DoesNotContain(displayed, point => Math.Abs(point.Offset - 20.0 / 7) < 1e-12);
        Assert.Null(Assert.Single(displayed, point => point.Offset == 4).Existing);
        Assert.Equal(2.4, Assert.Single(displayed, point => point.Offset == 4).Design!.Value, 12);
        Assert.Equal(5, raw.Points.Count);
        Assert.Null(raw.Points[1].Design);
    }

    [Fact]
    public void TrapezoidDisplayIncludesDaylightAndFootOrdinatesButNoProjectExtrapolation()
    {
        var raw = new CrossSection
        {
            DesignMode = SectionDesignMode.Trapezoid, ProjectBottom = 4, BottomWidth = 4,
            LeftSlope = 1, RightSlope = 1, CenterOffset = 0,
            Points = new List<SectionPoint>
            {
                new() { Offset = -10, Existing = 10 }, new() { Offset = 0, Existing = 10 },
                new() { Offset = 10, Existing = 10 }
            }
        };

        var displayed = SectionDisplayGeometry.Build(Earthworks.Calculate(raw));

        Assert.Equal(new[] { -10.0, -8, -2, 0, 2, 8, 10 }, displayed.Select(point => point.Offset));
        Assert.All(displayed, point => Assert.Equal(10, point.Existing));
        Assert.Null(displayed[0].Design);
        Assert.Null(displayed[^1].Design);
        Assert.Equal(new[] { 10.0, 4, 4, 4, 10 }, displayed.Skip(1).Take(5).Select(point => point.Design!.Value));
        Assert.Equal(3, raw.Points.Count);
        Assert.All(raw.Points, point => Assert.Null(point.Design));
    }

    [Fact]
    public void DistinctProjectKneesSurviveAtLargeSurveyOrigin()
    {
        const double origin = 900000000;
        var section = new CrossSection
        {
            DesignMode = SectionDesignMode.Trapezoid, ProjectBottom = 9, BottomWidth = .000001,
            LeftSlope = 1, RightSlope = 1, CenterOffset = origin,
            Points = new List<SectionPoint>
            {
                new() { Offset = origin - 2, Existing = 10 }, new() { Offset = origin, Existing = 10 },
                new() { Offset = origin + 2, Existing = 10 }
            }
        };
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Assert.True(result.DesignCurve[1].X < result.DesignCurve[2].X);

        var displayed = SectionDisplayGeometry.Build(result);

        Assert.Equal(7, displayed.Count);
        Assert.Contains(displayed, point => point.Offset == result.DesignCurve[1].X && point.Design == 9);
        Assert.Contains(displayed, point => point.Offset == result.DesignCurve[2].X && point.Design == 9);
        Assert.Contains(displayed, point => point.Offset == origin && point.Existing == 10);
        Assert.Equal(3, section.Points.Count);
    }

    [Fact]
    public void DisplayIntersectionUsesOriginalSurfaceWhenSiltChangesCutFillBoundary()
    {
        var raw = Section(new() { Offset = 0, Existing = 10, Design = 8 },
            new() { Offset = 10, Existing = 10, Design = 12 });
        raw.SiltThickness = 1;
        var result = Earthworks.Calculate(raw);
        Assert.Equal(1.25, result.CutArea);
        Assert.Equal(11.25, result.FillArea);

        var displayed = SectionDisplayGeometry.Build(result);

        var originalIntersection = Assert.Single(displayed, point => point.Offset == 5);
        Assert.Equal(10, originalIntersection.Existing);
        Assert.Equal(10, originalIntersection.Design);
        Assert.DoesNotContain(displayed, point => point.Offset == 2.5);
    }

    [Fact]
    public void SmallConnectedPatchStaysOneHatchWithUsableSpacingAndUnchangedArea()
    {
        var section = Section(new() { Offset = 0, Existing = .01, Design = 0 },
            new() { Offset = .002, Existing = .01 }, new() { Offset = .006, Existing = .01 },
            new() { Offset = .01, Existing = .01, Design = 0 });
        var project = new ProfileProject
        {
            Kind = ProfileKind.CrossSection, Sections = new List<CrossSection> { section },
            Settings = new ProfileSettings { Datum = -1, HorizontalScale = 1000, VerticalScale = 100 }
        };
        var plan = ProfileEngine.Build(project);
        var hatch = Assert.Single(plan.Entities.OfType<DrawHatch>(), entity => entity.Layer == "PROF_CUT_HATCH");
        double angle = hatch.PatternAngleDegrees * Math.PI / 180;
        var normal = hatch.Boundary.Select(point => -point.X * Math.Sin(angle) + point.Y * Math.Cos(angle)).ToList();
        Assert.True(hatch.PatternSpacing > 0);
        Assert.True(hatch.PatternSpacing < normal.Max() - normal.Min());
        double twice = 0;
        for (int index = 0; index < hatch.Boundary.Count; index++)
        {
            var a = hatch.Boundary[index];
            var b = hatch.Boundary[(index + 1) % hatch.Boundary.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        // H/V = 10 exaggerates vertical drawing dimensions; physical area remains 0.0001 m².
        Assert.Equal(.0001, Math.Abs(twice) / 2 / 10, 11);
        Assert.Equal(.0001, Earthworks.Calculate(section).CutArea!.Value, 12);
        Assert.Equal(4, section.Points.Count);
    }

    private static ProfileProject LinkedProject()
    {
        var project = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint>
            {
                new() { Station = 0, LeftBank = 10, RightBank = 12, ExistingBottom = 6, Water = 7,
                    ProjectBottom = 5, BottomWidth = 2, LeftSlope = 1, RightSlope = 1 }
            }
        };
        project.Sections.Add(ProfileInterpolation.CreateSection(project.Longitudinal, 0));
        return project;
    }

    private static ProfileProject MiddleStationProject(double? middleBottom)
    {
        var project = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint>
            {
                new() { Station = 0, ProjectBottom = 10, Water = 20 },
                new() { Station = 50, ProjectBottom = middleBottom, Water = 21 },
                new() { Station = 100, ProjectBottom = 20, Water = 22 }
            }
        };
        project.Sections.Add(ProfileInterpolation.CreateSection(project.Longitudinal, 50));
        return project;
    }

    private static CrossSection Section(params SectionPoint[] points) => new()
    {
        DesignMode = SectionDesignMode.Points, Points = points.ToList()
    };
}
