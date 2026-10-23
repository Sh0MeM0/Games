using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class LongitudinalVolumePresentationTests
{
    [Theory]
    [InlineData(ProfileTemplate.Reconstruction, false)]
    [InlineData(ProfileTemplate.Reconstruction, true)]
    [InlineData(ProfileTemplate.NewChannel, false)]
    [InlineData(ProfileTemplate.NewChannel, true)]
    [InlineData(ProfileTemplate.Nvk, false)]
    [InlineData(ProfileTemplate.Nvk, true)]
    public void EveryTemplateRetainsVolumeRowsWithoutAreaRowsEvenWhenNoSectionsExist(ProfileTemplate template, bool emptyRows)
    {
        var project = Project(0, 10);
        project.Template = template;
        project.Settings.ShowEmptyRows = emptyRows;
        var texts = Texts(project);
        foreach (string label in new[] { "ОБЪЁМ СРЕЗКИ, М³", "ОБЪЁМ ДОСЫПКИ, М³", "ОБЪЁМ ЗАИЛЕНИЯ, М³" })
            Assert.Single(texts, text => text.Text == label);
        Assert.DoesNotContain(texts, text => text.Text.Contains("ПЛОЩАДЬ"));
        Assert.Contains(texts, text => text.Text == "Срезка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Досыпка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Удаление ила = — (неполные данные)");
    }

    [Fact]
    public void TotalSumsFullPrecisionIntervalsBeforeRoundingOnce()
    {
        var project = Project(0, .12, Section(0), Section(.06), Section(.12));
        string original = ProjectFile.Serialize(project);
        var summary = Earthworks.CalculateVolumes(project);
        Assert.All(summary.Intervals, interval => Assert.Equal(.06, interval.CutVolume!.Value, 12));
        Assert.Equal(.12, summary.TotalCutVolume!.Value, 12);
        var texts = Texts(project);
        var row = Assert.Single(texts, text => text.Text == "ОБЪЁМ СРЕЗКИ, М³");
        var values = texts.Where(text => text.Position.X >= 0 && text.Position.X <= .12 && Math.Abs(text.Position.Y - row.Position.Y) < 1e-8).ToList();
        Assert.Equal(2, values.Count);
        Assert.All(values, text => Assert.Equal("0.1", text.Text));
        Assert.Contains(texts, text => text.Text == "Срезка = 0.1 м³" && text.Position.X > .12);
        Assert.DoesNotContain(texts, text => text.Text == "Срезка = 0.2 м³");
        Assert.Equal(original, ProjectFile.Serialize(project));
    }

    [Fact]
    public void FullIntervalTotalsExcludeWhollyOutsideIntervals()
    {
        var project = Project(0, 10, Section(-10), Section(0), Section(10), Section(20));
        Assert.Equal(30, Earthworks.CalculateVolumes(project).TotalCutVolume);
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "Срезка = 10.0 м³");
        Assert.DoesNotContain(texts, text => text.Text == "Срезка = 30.0 м³");
        Assert.Single(texts, text => text.Text == "Итого по показанному участку");
    }

    [Fact]
    public void InvalidOutsideSectionsDoNotInvalidateCompletelyMeasuredDisplayedSpan()
    {
        var outside = Section(-10);
        outside.Points.Clear();
        var project = Project(0, 10, outside, Section(0), Section(10));
        Assert.Equal(10, Earthworks.CalculateVolumes(project).TotalCutVolume);
        Assert.Contains(Texts(project), text => text.Text == "Срезка = 10.0 м³");
    }

    [Theory]
    [InlineData(-10, 10)]
    [InlineData(0, 20)]
    [InlineData(-10, 20)]
    public void IntervalsCrossingDisplayedEndsAreNotClippedIntoInventedVolumes(double left, double right)
    {
        var project = Project(0, 10, Section(left), Section(right));
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "Срезка = — (неполные данные)");
        Assert.DoesNotContain(texts, text => text.Text == "Срезка = 10.0 м³");
        if (left < 0 || right > 10)
        {
            var row = Assert.Single(texts, text => text.Text == "ОБЪЁМ СРЕЗКИ, М³");
            Assert.DoesNotContain(texts, text => text.Position.X >= 0 && text.Position.X <= 10 && Math.Abs(text.Position.Y - row.Position.Y) < 1e-8);
        }
    }

    [Fact]
    public void DuplicateSectionBlocksItsNeighborsWhileUnassignedSurveyAllowsKnownSubtotal()
    {
        var duplicate = Project(0, 10, Section(0), Section(5), Section(5), Section(10));
        Assert.Contains(Texts(duplicate), text => text.Text == "Срезка = — (неполные данные)");
        var unsited = Section(5); unsited.Station = null;
        var project = Project(0, 10, Section(0), unsited, Section(10));
        Assert.True(Earthworks.CalculateVolumes(project).HasUnassignedSections);
        Assert.Contains(Texts(project), text => text.Text == "Срезка = 10.0 м³");
        Assert.Contains(Texts(project), text => text.Text == "По рассчитанным участкам");
        project.Sections.Remove(unsited);
        project.Sections.Add(new CrossSection());
        Assert.False(Earthworks.CalculateVolumes(project).HasUnassignedSections);
        Assert.Contains(Texts(project), text => text.Text == "Срезка = 10.0 м³");
    }

    [Fact]
    public void IndependentQuantitiesKeepSiltSeparateFromAdditionalCut()
    {
        var first = Section(0); first.SiltThickness = .25;
        var last = Section(10); last.SiltThickness = .25;
        var texts = Texts(Project(0, 10, first, last));
        Assert.Contains(texts, text => text.Text == "Срезка = 7.5 м³");
        Assert.Contains(texts, text => text.Text == "Удаление ила = 2.5 м³");
        Assert.Contains(texts, text => text.Text == "Досыпка = 0.0 м³");
        Assert.DoesNotContain(texts, text => text.Text.Contains("Wмин") || text.Text.Contains("грунт"));
    }

    [Fact]
    public void UnknownCutAndFillCannotBeReplacedByZeroWhenSiltIsKnown()
    {
        var first = Section(0); first.DesignMode = SectionDesignMode.None; first.SiltThickness = .25;
        var last = Section(10); last.DesignMode = SectionDesignMode.None; last.SiltThickness = .25;
        var texts = Texts(Project(0, 10, first, last));
        Assert.Contains(texts, text => text.Text == "Срезка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Досыпка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Удаление ила = 2.5 м³");
    }

    private static List<DrawText> Texts(ProfileProject project) => ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();

    private static ProfileProject Project(double first, double last, params CrossSection[] sections) => new()
    {
        Kind = ProfileKind.Longitudinal,
        Settings = new ProfileSettings { Datum = 0, HorizontalScale = 1000, VerticalScale = 100 },
        Longitudinal = new List<LongitudinalPoint> { new() { Station = first, Ground = 10 }, new() { Station = last, Ground = 10 } },
        Sections = sections.ToList()
    };

    private static CrossSection Section(double station) => new()
    {
        Station = station, Name = "Промер", DesignMode = SectionDesignMode.Points,
        Points = new List<SectionPoint>
        {
            new() { Offset = 0, Existing = 10, Design = 9 },
            new() { Offset = 1, Existing = 10, Design = 9 }
        }
    };
}
