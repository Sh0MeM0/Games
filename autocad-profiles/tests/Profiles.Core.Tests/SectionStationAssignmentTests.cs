using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class SectionStationAssignmentTests
{
    [Theory]
    [InlineData("0+20", 20)]
    [InlineData("20", 20)]
    [InlineData(" ПК 0+20,50 ", 20.5)]
    [InlineData("пк 20", 20)]
    [InlineData("1+25.5", 125.5)]
    public void NumericSectionNamesAreStations(string name, double expected)
    {
        Assert.True(SectionStationAssignment.TryParseName(name, out var station));
        Assert.Equal(expected, station);
    }

    [Theory]
    [InlineData("Промер 1")]
    [InlineData("Сечение 20")]
    [InlineData("0+120")]
    [InlineData("NaN")]
    [InlineData("1000000001")]
    [InlineData("")]
    public void OrdinaryNamesAndInvalidStationsAreNotAssigned(string name) =>
        Assert.False(SectionStationAssignment.TryParseName(name, out _));

    [Fact]
    public void AssigningImportedSurveyToExistingStationCopiesMarksAndKeepsPointsAndOtherStationFields()
    {
        var source = Project();
        string before = ProjectFile.Serialize(source);
        var conflicts = SectionStationAssignment.ConflictingFields(source, 0, 20);
        Assert.Equal(new[] { "левый берег" }, conflicts);
        var result = SectionStationAssignment.Assign(source, 0, 20);
        Assert.Equal(2, result.Longitudinal.Count);
        var row = result.Longitudinal[0];
        Assert.Equal(111.5, row.LeftBank);
        Assert.Equal(109.25, row.ExistingBottom);
        Assert.Equal(112.5, row.RightBank);
        Assert.Equal(108, row.ProjectBottom);
        Assert.Equal("Исходное имя точки", row.PointName);
        Assert.Equal("ПК 0+20.00", result.Sections[0].Name);
        Assert.Equal(20, result.Sections[0].Station);
        Assert.Equal(source.Sections[0].SectionId, result.Sections[0].SectionId);
        Assert.Equal(source.Sections[0].Points.Select(p => p.DistanceFromPrevious), result.Sections[0].Points.Select(p => p.DistanceFromPrevious));
        Assert.Equal(source.Sections[0].Points.Select(p => p.Existing), result.Sections[0].Points.Select(p => p.Existing));
        Assert.Equal(before, ProjectFile.Serialize(source));
        Assert.Contains(ProfileEngine.Build(result).Entities.OfType<DrawText>(), text => text.Text == "111.50");
    }

    [Fact]
    public void NewStationIsAddedInOrderAndSurvivesRoundtrip()
    {
        var source = Project();
        var result = SectionStationAssignment.Assign(source, 0, 10);
        result = ProjectFile.Deserialize(ProjectFile.Serialize(result));
        Assert.Equal(new double[] { 10, 20, 40 }, result.Longitudinal.Select(p => p.Station));
        Assert.Equal(109.25, result.Longitudinal[0].ExistingBottom);
        Assert.Equal(10, result.Sections[0].Station);
    }

    [Fact]
    public void ExistingDifferentCrossSectionIsNeverReplaced()
    {
        var source = Project();
        source.Sections.Add(new CrossSection { Station = 20, Name = "Другой промер" });
        string before = ProjectFile.Serialize(source);
        Assert.Throws<ProfileValidationException>(() => SectionStationAssignment.Assign(source, 0, 20));
        Assert.Equal(before, ProjectFile.Serialize(source));
        Assert.Null(source.Sections[0].Station);
    }

    private static ProfileProject Project()
    {
        var section = DrawingSurvey.BuildSection(new List<SectionSurveyPick>
        {
            new() { Position = new Point2(0, 0), Values = new SectionPoint { Existing = 111.5 } },
            new() { Position = new Point2(2, 0), Values = new SectionPoint { Existing = 109.25 } },
            new() { Position = new Point2(5, 0), Values = new SectionPoint { Existing = 112.5 } }
        }, SurveySourceUnits.ModelMetres(), null, "Промер 1");
        return new ProfileProject
        {
            Kind = ProfileKind.Longitudinal,
            Longitudinal = new List<LongitudinalPoint> { new() { Station = 20, LeftBank = 110, ProjectBottom = 108, PointName = "Исходное имя точки" }, new() { Station = 40, LeftBank = 112, ExistingBottom = 109, RightBank = 113, ProjectBottom = 108 } },
            Sections = new List<CrossSection> { section }
        };
    }
}
