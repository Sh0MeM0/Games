using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class PartialVolumeTotalsTests
{
    [Theory]
    [InlineData(0, 6)]
    [InlineData(2, 8)]
    [InlineData(4, 10)]
    public void SurveyedPartHasItsOwnTotalWithoutInventingUnsurveyedEnds(double first, double last)
    {
        var project = Project(0, 10, Section(first), Section(last));
        string before = ProjectFile.Serialize(project);
        var summary = Earthworks.CalculateVolumes(project);
        var interval = Assert.Single(summary.Intervals);
        Assert.Equal(first, interval.StartStation);
        Assert.Equal(last, interval.EndStation);
        Assert.Equal(6, summary.TotalCutVolume);
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "По рассчитанным участкам");
        Assert.Contains(texts, text => text.Text == "Срезка = 6.0 м³");
        Assert.Contains(texts, text => text.Text == "Досыпка = 0.0 м³");
        Assert.Equal("6.0", Assert.Single(RowValues(texts, "ОБЪЁМ СРЕЗКИ, М³", 10)).Text);
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrDuplicateMiddleSectionBlocksItsNeighborsButNotOtherIntervals(bool duplicate)
    {
        var middle = Section(20);
        if (!duplicate) middle.Points.Clear();
        var project = Project(0, 50, Section(0), Section(10), middle, Section(30), Section(50));
        if (duplicate) project.Sections.Add(Section(20));
        var summary = Earthworks.CalculateVolumes(project);
        Assert.Equal(4, summary.Intervals.Count);
        Assert.Null(summary.Intervals[1].CutVolume);
        Assert.Null(summary.Intervals[2].CutVolume);
        Assert.Equal(30, summary.TotalCutVolume);
        Assert.Equal(0, summary.TotalFillVolume);
        Assert.DoesNotContain(summary.Intervals, interval => interval.StartStation == 10 && interval.EndStation == 30);
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "По рассчитанным участкам");
        Assert.Contains(texts, text => text.Text == "Срезка = 30.0 м³");
        Assert.Equal(new[] { "10.0", "20.0" }, RowValues(texts, "ОБЪЁМ СРЕЗКИ, М³", 50).Select(text => text.Text));
    }

    [Fact]
    public void EachQuantitySumsOnlyItsOwnKnownIntervals()
    {
        var project = Project(0, 40, Section(0), Section(10), Section(20), Section(30));
        foreach (var section in project.Sections) section.SiltThickness = .25;
        project.Sections[0].DesignMode = SectionDesignMode.None;
        project.Sections[1].DesignMode = SectionDesignMode.None;
        var summary = Earthworks.CalculateVolumes(project);
        Assert.Equal(7.5, summary.TotalCutVolume);
        Assert.Equal(0, summary.TotalFillVolume);
        Assert.Equal(7.5, summary.TotalSiltVolume);
        Assert.Null(summary.Intervals[0].CutVolume);
        Assert.Null(summary.Intervals[1].CutVolume);
        Assert.All(summary.Intervals, interval => Assert.Equal(2.5, interval.SiltVolume));
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "Срезка = 7.5 м³");
        Assert.Contains(texts, text => text.Text == "Удаление ила = 7.5 м³");
        Assert.Single(RowValues(texts, "ОБЪЁМ СРЕЗКИ, М³", 40));
        Assert.Equal(3, RowValues(texts, "ОБЪЁМ ЗАИЛЕНИЯ, М³", 40).Count);
    }

    [Fact]
    public void AQuantityWithNoCalculatedIntervalStaysUnknownWhileKnownZeroIsReported()
    {
        var project = Project(0, 10, Section(2), Section(8));
        foreach (var section in project.Sections) section.DesignMode = SectionDesignMode.None;
        var summary = Earthworks.CalculateVolumes(project);
        Assert.Null(summary.TotalCutVolume);
        Assert.Null(summary.TotalFillVolume);
        Assert.Equal(0, summary.TotalSiltVolume);
        var texts = Texts(project);
        Assert.Contains(texts, text => text.Text == "Срезка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Досыпка = — (неполные данные)");
        Assert.Contains(texts, text => text.Text == "Удаление ила = 0.0 м³");
        Assert.Empty(RowValues(texts, "ОБЪЁМ СРЕЗКИ, М³", 10));
    }

    [Fact]
    public void OneMeasuredSectionCannotProduceEvenAZeroVolume()
    {
        var summary = Earthworks.CalculateVolumes(Project(0, 10, Section(5)));
        Assert.Empty(summary.Intervals);
        Assert.Null(summary.TotalCutVolume);
        Assert.Null(summary.TotalFillVolume);
        Assert.Null(summary.TotalSiltVolume);
    }

    [Fact]
    public void InactiveProfileSectionsDoNotFillGapsInTheActiveProfile()
    {
        var old = Project(0, 10, Section(0), Section(10));
        var active = ProfileCollection.Add(old, "Частичная съёмка");
        active.Longitudinal = old.Longitudinal;
        active.Sections.AddRange(new[] { Section(2), Section(8) });
        var summary = Earthworks.CalculateVolumes(active);
        Assert.Single(summary.Intervals);
        Assert.Equal(6, summary.TotalCutVolume);
        Assert.Contains(Texts(active), text => text.Text == "По рассчитанным участкам");
        Assert.Contains(Texts(active), text => text.Text == "Срезка = 6.0 м³");
        Assert.Equal(10, Earthworks.CalculateVolumes(ProfileCollection.Select(active, old.ProfileId)).TotalCutVolume);
    }

    private static List<DrawText> Texts(ProfileProject project) => ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();

    private static List<DrawText> RowValues(List<DrawText> texts, string label, double length)
    {
        var row = Assert.Single(texts, text => text.Text == label);
        return texts.Where(text => text.Position.X >= 0 && text.Position.X <= length && Math.Abs(text.Position.Y - row.Position.Y) < 1e-8).ToList();
    }

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
