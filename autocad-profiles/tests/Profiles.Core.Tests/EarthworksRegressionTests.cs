using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

/// <summary>Independent analytic checks; expected areas are derived in physical metres.</summary>
public sealed class EarthworksRegressionTests
{
    private static CrossSection Flat(double width = 10, double existing = 10, double design = 9)
    {
        return new CrossSection
        {
            Name = "Расчётное сечение",
            Points = new List<SectionPoint>
            {
                new SectionPoint { Offset = 0, Existing = existing, Design = design },
                new SectionPoint { Offset = width, Existing = existing, Design = design }
            }
        };
    }

    private static void Area(double expected, double? actual, double tolerance = 1e-9)
    {
        Assert.True(actual.HasValue, "Area/volume must be available for this complete analytic fixture.");
        Assert.InRange(actual.Value, expected - tolerance, expected + tolerance);
    }

    private static double PolygonArea(IEnumerable<Point2> source)
    {
        var points = source.ToList();
        double twice = 0;
        for (int index = 0; index < points.Count; index++)
        {
            var a = points[index];
            var b = points[(index + 1) % points.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(twice) / 2;
    }

    [Fact]
    public void CrossingSurfacesAreSplitIntoIndependentCutAndFillTriangles()
    {
        var section = Flat();
        section.Points[0].Design = 8;
        section.Points[1].Design = 12;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        // Difference +2 to -2 crosses at x=5: each triangle is 5*2/2.
        Area(5, result.CutArea);
        Area(5, result.FillArea);
        Area(0, result.SiltArea);
        Area(5, result.CutRegions.Sum(PolygonArea));
        Area(5, result.FillRegions.Sum(PolygonArea));
    }

    [Fact]
    public void SiltIsRemovedBeforeCutFillComparisonRatherThanAddedToOriginalCut()
    {
        var section = Flat();
        section.Points[0].Design = 8;
        section.Points[1].Design = 12;
        section.SiltThickness = 1;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        // Cleaned elevation is 9: difference +1 to -3 crosses at x=2.5.
        Area(10, result.SiltArea);
        Area(1.25, result.CutArea);
        Area(11.25, result.FillArea);
        Area(0, result.CutArea + result.SiltArea - result.FillArea);
    }

    [Fact]
    public void RemovingSiltBelowFillIncreasesRequiredFillWithoutCountingItTwice()
    {
        var section = Flat(width: 4, design: 11);
        section.SiltThickness = 0.5;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Area(2, result.SiltArea);
        Area(0, result.CutArea);
        Area(6, result.FillArea);
        // Net imported material remains 4m²: fill6 minus removed silt2.
        Area(4, result.FillArea - result.SiltArea - result.CutArea);
    }

    [Fact]
    public void PartialSiltRangeUsesVerticalEdgesWithoutInventingOutsideRamps()
    {
        var section = Flat();
        section.SiltThickness = 0.5;
        section.SiltLeft = 2;
        section.SiltRight = 8;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Area(3, result.SiltArea); // 6m wide * 0.5m vertical thickness.
        Area(7, result.CutArea);  // Outside:4*1; inside:6*0.5.
        Area(0, result.FillArea);
        Assert.All(result.SiltRegions.SelectMany(region => region), point => Assert.InRange(point.X, 2, 8));
        Area(3, result.SiltRegions.Sum(PolygonArea));
        Area(7, result.CutRegions.Sum(PolygonArea));
    }

    [Fact]
    public void ExistingAndProjectKnotsAreMergedBeforeIntegration()
    {
        var section = new CrossSection
        {
            Points = new List<SectionPoint>
            {
                new SectionPoint { Offset = 0, Existing = 10, Design = 9 },
                new SectionPoint { Offset = 4, Existing = 12, Design = null },
                new SectionPoint { Offset = 6, Existing = 32.0 / 3, Design = 11 },
                new SectionPoint { Offset = 10, Existing = 8, Design = 10 }
            }
        };
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        // On [0,4] cut16/3; on[4,6] cut25/18 and fill1/18;
        // on[6,10] fill14/3. Curves intersect at x=17/3.
        Area(121.0 / 18, result.CutArea);
        Area(85.0 / 18, result.FillArea);
        Area(2, result.CutArea - result.FillArea);
    }

    [Fact]
    public void SiltThicknessIsVerticalEvenOnAnInclinedSurvey()
    {
        var section = Flat(width: 3);
        section.Points[0].Existing = 0;
        section.Points[1].Existing = 4;
        section.Points[0].Design = -1;
        section.Points[1].Design = 3;
        section.SiltThickness = 0.2;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Area(0.6, result.SiltArea); // Horizontal3*.2, not sloping length5*.2.
        Area(2.4, result.CutArea);
        Area(0, result.FillArea);
    }

    [Fact]
    public void CleaningDoesNotMoveTheFixedProjectProfileAtItsBankIntersections()
    {
        var section = new CrossSection
        {
            SiltThickness = 1,
            Points = new List<SectionPoint>
            {
                new SectionPoint { Offset = -8, Existing = 10, Design = 10 },
                new SectionPoint { Offset = -2, Existing = 10, Design = 4 },
                new SectionPoint { Offset = 2, Existing = 10, Design = 4 },
                new SectionPoint { Offset = 8, Existing = 10, Design = 10 }
            }
        };
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Area(16, result.SiltArea);
        Area(45, result.CutArea); // 4*5 + 2*(5*5/2).
        Area(1, result.FillArea); // Two outer 1*1/2 triangles after cleaning.
        Area(60, result.CutArea + result.SiltArea - result.FillArea);
    }

    [Fact]
    public void VeryThinSiltIsRetainedInRawQuantitiesEvenWhenF2WouldDisplayZero()
    {
        var section = Flat(width: 1000, existing: 1, design: 0);
        section.SiltThickness = 1e-8;
        var result = Earthworks.Calculate(section);
        Assert.True(result.IsValid, result.Status);
        Area(0.00001, result.SiltArea, 1e-12);
        Area(999.99999, result.CutArea, 1e-9);
        Assert.True(result.SiltArea > 0);
    }

    [Fact]
    public void MissingSurveyCoverageCannotBeConvertedToZeroOrBridgedForQuantities()
    {
        var section = Flat();
        section.Points.Insert(1, new SectionPoint { Offset = 5, Existing = null, Design = 9 });
        var result = Earthworks.Calculate(section);
        Assert.False(result.IsValid);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
        Assert.False(string.IsNullOrWhiteSpace(result.Status));
    }

    [Fact]
    public void SparseProjectDoesNotExtrapolatePastItsKnownAnchors()
    {
        var section = Flat();
        section.Points[0].Design = null;
        var result = Earthworks.Calculate(section);
        Assert.False(result.IsValid);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
    }

    private static ProfileProject VolumeProject(params CrossSection[] sections) =>
        new ProfileProject { Kind = ProfileKind.CrossSection, Sections = sections.ToList() };

    [Fact]
    public void VolumesUseAveragePhysicalAreasAndActualUnequalStationSpacing()
    {
        var first = Flat(width: 4); first.Station = 0; first.SiltThickness = 0.25;
        var middle = Flat(width: 4); middle.Station = 10; middle.SiltThickness = 0.5;
        var last = Flat(width: 4); last.Station = 30; last.SiltThickness = 0.75;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, middle, last));
        Assert.Equal(2, result.Intervals.Count);
        Area(25, result.Intervals[0].CutVolume);
        Area(30, result.Intervals[1].CutVolume);
        Area(15, result.Intervals[0].SiltVolume);
        Area(50, result.Intervals[1].SiltVolume);
        Area(55, result.TotalCutVolume);
        Area(65, result.TotalSiltVolume);
        Area(0, result.TotalFillVolume);
    }

    [Fact]
    public void IntermediateLongitudinalRowsWithoutSectionsDoNotInventExtraVolumeIntervals()
    {
        var first = Flat(); first.Station = 0;
        var last = Flat(); last.Station = 10;
        var project = VolumeProject(first, last);
        project.Longitudinal = new List<LongitudinalPoint>
        {
            new LongitudinalPoint { Station = 0 }, new LongitudinalPoint { Station = 5 }, new LongitudinalPoint { Station = 10 }
        };
        var result = Earthworks.CalculateVolumes(project);
        Assert.Single(result.Intervals);
        Area(100, result.TotalCutVolume);
    }

    [Fact]
    public void DuplicateSectionStationBlocksBothNeighborsAndWholeProjectTotal()
    {
        var first = Flat(); first.Station = 0;
        var duplicateA = Flat(); duplicateA.Station = 5;
        var duplicateB = Flat(); duplicateB.Station = 5;
        var last = Flat(); last.Station = 10;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, duplicateA, duplicateB, last));
        Assert.Null(result.TotalCutVolume);
        Assert.Null(result.TotalFillVolume);
        Assert.Null(result.TotalSiltVolume);
        Assert.NotEmpty(result.Warnings);
        Assert.All(result.Intervals, interval => Assert.Null(interval.CutVolume));
    }

    [Fact]
    public void ExplicitInvalidSectionBetweenValidStationsIsNotSkipped()
    {
        var first = Flat(); first.Station = 0;
        var invalid = Flat(); invalid.Station = 5; invalid.Points[1].Existing = null;
        var last = Flat(); last.Station = 10;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, invalid, last));
        Assert.Null(result.TotalCutVolume);
        Assert.Equal(2, result.Intervals.Count);
        Assert.All(result.Intervals, interval => Assert.Null(interval.CutVolume));
        Assert.DoesNotContain(result.Intervals, interval => interval.StartStation == 0 && interval.EndStation == 10);
    }

    [Fact]
    public void MeaningfulSectionWithUnknownStationWarnsButRetainsKnownSubtotalWithoutInventedPosition()
    {
        var first = Flat(); first.Station = 0;
        var unsited = Flat();
        var last = Flat(); last.Station = 10;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, unsited, last));
        Area(100, result.TotalCutVolume);
        Area(0, result.TotalFillVolume);
        Area(0, result.TotalSiltVolume);
        Assert.NotEmpty(result.Warnings);
        Assert.All(result.Intervals, interval =>
        {
            Assert.Equal(0, interval.StartStation);
            Assert.Equal(10, interval.EndStation);
        });
    }

    [Fact]
    public void EmptyEditorPlaceholderDoesNotInvalidateKnownVolumes()
    {
        var first = Flat(); first.Station = 0;
        var last = Flat(); last.Station = 10;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, new CrossSection(), last));
        Area(100, result.TotalCutVolume);
    }

    [Fact]
    public void RawPrecisionIsNotRoundedToDisplayedTwoDecimalsBeforeVolumeIntegration()
    {
        var first = Flat(width: 1, design: 10 - 1.23456789); first.Station = 0;
        var last = Flat(width: 1, design: 10 - 3.45678912); last.Station = 0.123456789;
        var result = Earthworks.CalculateVolumes(VolumeProject(first, last));
        double expected = (double)((1.23456789m + 3.45678912m) / 2m * 0.123456789m);
        Area(expected, result.TotalCutVolume, 1e-11);
    }

    [Fact]
    public void QuantitiesStayInPhysicalUnitsRegardlessOfDrawingScaleAndUnits()
    {
        var first = Flat(); first.Station = 0; first.SiltThickness = 0.25;
        var last = Flat(); last.Station = 10; last.SiltThickness = 0.5;
        var project = VolumeProject(first, last);
        var metres = Earthworks.CalculateVolumes(project);
        project.Settings.HorizontalScale = 200;
        project.Settings.VerticalScale = 50;
        project.Settings.MetresPerDrawingUnit = 0.001;
        project.Settings.Datum = -150;
        var millimetres = Earthworks.CalculateVolumes(project);
        Area(metres.TotalCutVolume.Value, millimetres.TotalCutVolume);
        Area(metres.TotalFillVolume.Value, millimetres.TotalFillVolume);
        Area(metres.TotalSiltVolume.Value, millimetres.TotalSiltVolume);
    }

    [Theory]
    [InlineData(SectionDesignMode.Trapezoid, false)]
    [InlineData(SectionDesignMode.Points, false)]
    [InlineData(SectionDesignMode.Points, true)]
    public void IncompleteSelectedProjectCannotDefaultSiltToTheWholeMeasuredWidth(SectionDesignMode mode, bool singleAnchor)
    {
        var section = Flat();
        section.DesignMode = mode;
        section.Points.ForEach(point => point.Design = null);
        if (mode == SectionDesignMode.Trapezoid) section.ProjectBottom = 8; // Width and slopes still unknown.
        if (singleAnchor) section.Points[0].Design = 8;
        section.SiltThickness = 0.5;
        var result = Earthworks.Calculate(section);
        Assert.Null(result.SiltArea);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void ExplicitSiltBoundsRemainIndependentlyCalculableWithAnIncompleteProject()
    {
        var section = Flat();
        section.DesignMode = SectionDesignMode.Trapezoid;
        section.ProjectBottom = 8;
        section.SiltThickness = 0.5;
        section.SiltLeft = 2;
        section.SiltRight = 8;
        var result = Earthworks.Calculate(section);
        Area(3, result.SiltArea);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
    }

    [Fact]
    public void OneSiltBoundCannotBeCompletedFromSurveyWhenSelectedProjectIsUnknown()
    {
        var section = Flat();
        section.DesignMode = SectionDesignMode.Trapezoid;
        section.ProjectBottom = 8;
        section.SiltThickness = 0.5;
        section.SiltLeft = 2;
        Assert.Null(Earthworks.Calculate(section).SiltArea);
    }

    [Fact]
    public void ExplicitlyDisabledProjectAllowsSiltToUseWholeMeasuredWidth()
    {
        var section = Flat();
        section.DesignMode = SectionDesignMode.None;
        section.SiltThickness = 0.5;
        var result = Earthworks.Calculate(section);
        Area(5, result.SiltArea);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
    }
}
