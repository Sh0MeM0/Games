using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class SectionDraftAndMigrationTests
{
    private static CrossSection DistanceSection() => new CrossSection
    {
        UsePointDistances = true, DesignMode = SectionDesignMode.Points, ProjectAnchorIndex = 1,
        Points = new List<SectionPoint>
        {
            new() { Offset = -4, DistanceFromPrevious = 0, Existing = 10, Design = 8 },
            new() { Offset = 99, DistanceFromPrevious = 3.25, Existing = 8 },
            new() { Offset = 99, DistanceFromPrevious = 7.25, Existing = 10, Design = 9 }
        }
    };

    [Fact]
    public void DistancesResolveOnCopyWithoutInventingOrOverwritingRawOffsets()
    {
        var raw = DistanceSection();
        var resolved = ProfileInterpolation.ResolveSection(raw);
        Assert.Equal(new[] { -4.0, -0.75, 6.5 }, resolved.Points.Select(point => point.Offset));
        Assert.Equal(-0.75, resolved.CenterOffset);
        Assert.False(resolved.UsePointDistances);
        Assert.Equal(99, raw.Points[1].Offset);
        Assert.Equal(0, raw.CenterOffset);
        Assert.Null(raw.Points[1].Design);
        Assert.Equal(8 + 3.25 / 10.5, resolved.Points[1].Design!.Value, 12);
    }

    [Fact]
    public void IncompleteDistanceDraftRoundtripsAndDoesNotCalculateZeroWidth()
    {
        var raw = DistanceSection(); raw.Points[1].DistanceFromPrevious = null;
        var project = new ProfileProject { Kind = ProfileKind.CrossSection, Sections = new List<CrossSection> { raw } };
        var restored = ProjectFile.Clone(project);
        Assert.Null(restored.Sections[0].Points[1].DistanceFromPrevious);
        Assert.Equal(99, restored.Sections[0].Points[1].Offset);
        var result = Earthworks.Calculate(restored.Sections[0]);
        Assert.False(result.IsValid);
        Assert.Null(result.CutArea);
        Assert.Null(result.FillArea);
        Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.ResolveSection(raw));
        Assert.Null(raw.Points[1].DistanceFromPrevious);
    }

    [Fact]
    public void LinkedStationRolesWaterAndProjectBottomUpdateButManualPointWins()
    {
        var longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, LeftBank = 12, ExistingBottom = 8, RightBank = 13, Water = 9, ProjectBottom = 7, BottomWidth = 2, LeftSlope = 1, RightSlope = 1 },
            new() { Station = 100, LeftBank = 16, ExistingBottom = 12, RightBank = 17, Water = 13, ProjectBottom = 11 }
        };
        var section = ProfileInterpolation.CreateSection(longitudinal, 0);
        section.Points[1].DistanceFromPrevious = 5;
        section.Points[2].DistanceFromPrevious = 5;
        section.Points[1].UseLongitudinalElevation = false;
        section.Points[1].Existing = 8.5;
        longitudinal[0].LeftBank = 15;
        longitudinal[0].ExistingBottom = 9;
        longitudinal[0].Water = 10;
        longitudinal[0].ProjectBottom = 8;
        var resolved = ProfileInterpolation.ResolveSection(section, longitudinal);
        Assert.Equal(15, resolved.Points[0].Existing);
        Assert.Equal(8.5, resolved.Points[1].Existing);
        Assert.Equal(10, resolved.Water);
        Assert.Equal(8, resolved.ProjectBottom);
        Assert.Equal(12, section.Points[0].Existing);
        Assert.Equal(9, section.Water);
        Assert.Equal(7, section.ProjectBottom);
        section.UseLongitudinalWater = false;
        Assert.Equal(9, ProfileInterpolation.ResolveSection(section, longitudinal).Water);
    }

    [Fact]
    public void MissingLinkedElevationDoesNotReuseStaleImportedHeight()
    {
        var longitudinal = new List<LongitudinalPoint> { new() { Station = 0, LeftBank = 12 } };
        var section = ProfileInterpolation.CreateSection(longitudinal, 0);
        section.Points[1].DistanceFromPrevious = 3;
        section.Points[2].DistanceFromPrevious = 3;
        longitudinal[0].LeftBank = null;
        Assert.Null(ProfileInterpolation.ResolveSection(section, longitudinal).Points[0].Existing);
        Assert.Equal(12, section.Points[0].Existing);
    }

    [Fact]
    public void ExplicitDesignModesPreserveInactiveInputAndOnlyResolveSelectedGeometry()
    {
        var section = DistanceSection();
        section.ProjectBottom = 7; section.BottomWidth = 2; section.LeftSlope = 1; section.RightSlope = 1;
        var points = ProfileInterpolation.ResolveSection(section);
        Assert.Null(points.ProjectBottom);
        Assert.NotNull(points.Points[0].Design);
        section.DesignMode = SectionDesignMode.Trapezoid;
        var trapezoid = ProfileInterpolation.ResolveSection(section);
        Assert.Equal(7, trapezoid.ProjectBottom);
        Assert.All(trapezoid.Points, point => Assert.Null(point.Design));
        section.DesignMode = SectionDesignMode.None;
        var none = ProfileInterpolation.ResolveSection(section);
        Assert.Null(none.ProjectBottom);
        Assert.All(none.Points, point => Assert.Null(point.Design));
        Assert.Equal(7, section.ProjectBottom);
        Assert.Equal(8, section.Points[0].Design);
    }

    [Fact]
    public void SiltCanBeCalculatedIndependentlyWithoutProjectButFailedProjectNeverGuessesWidth()
    {
        var section = DistanceSection(); section.DesignMode = SectionDesignMode.None; section.SiltThickness = 0.5;
        var noProject = Earthworks.Calculate(section);
        Assert.Null(noProject.CutArea);
        Assert.Null(noProject.FillArea);
        Assert.Equal(5.25, noProject.SiltArea);
        section.DesignMode = SectionDesignMode.Trapezoid; section.ProjectBottom = 7;
        var unfinished = Earthworks.Calculate(section);
        Assert.Null(unfinished.SiltArea);
        section.SiltLeft = -2; section.SiltRight = 3;
        Assert.Equal(2.5, Earthworks.Calculate(section).SiltArea);
    }

    [Fact]
    public void SchemaTwoMigratesIdentityAndLegacyOffsetsWithoutCreatingLinks()
    {
        var project = new ProfileProject
        {
            Name = "Старый проект", Kind = ProfileKind.CrossSection,
            Sections = new List<CrossSection>
            {
                new() { Points = new List<SectionPoint> { new() { Offset = -3, Existing = 10, Design = 9 }, new() { Offset = 7, Existing = 10, Design = 9 } } }
            }
        };
        var document = JsonNode.Parse(ProjectFile.Serialize(project))!.AsObject();
        document["SchemaVersion"] = 2;
        SchemaTestDocuments.RemoveVersionFiveFields(document);
        document["Settings"]!.AsObject().Remove("CrossHorizontalScale");
        document["Settings"]!.AsObject().Remove("CrossVerticalScale");
        foreach (var item in document["Sections"]!.AsArray())
        {
            var section = item!.AsObject();
            foreach (string name in new[] { "DesignMode", "UsePointDistances", "ProjectAnchorIndex", "UseLongitudinalProjectBottom", "UseLongitudinalWater", "SiltThickness", "SiltLeft", "SiltRight" }) section.Remove(name);
            foreach (var point in section["Points"]!.AsArray())
                foreach (string name in new[] { "Role", "DistanceFromPrevious", "UseLongitudinalElevation" }) point!.AsObject().Remove(name);
        }
        var migrated = ProjectFile.Deserialize(document.ToJsonString());
        Assert.Equal(5, migrated.SchemaVersion);
        Assert.Equal(project.ProjectId, migrated.ProjectId);
        Assert.Equal(project.Name, migrated.Name);
        Assert.False(migrated.Sections[0].UsePointDistances);
        Assert.False(migrated.Sections[0].UseLongitudinalProjectBottom);
        Assert.Equal(SectionDesignMode.Points, migrated.Sections[0].DesignMode);
        Assert.Equal(new[] { -3.0, 7 }, migrated.Sections[0].Points.Select(point => point.Offset));
        Assert.All(migrated.Sections[0].Points, point =>
        {
            Assert.Equal(SectionPointRole.Regular, point.Role);
            Assert.Null(point.DistanceFromPrevious);
            Assert.False(point.UseLongitudinalElevation);
        });
        document["Sections"]![0]!["UsePointDistances"] = true;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void SchemaThreePreservesRolesDistancesAnchorModesAndSiltBounds()
    {
        var section = DistanceSection();
        section.Points[0].Role = SectionPointRole.LeftBank;
        section.Points[0].UseLongitudinalElevation = true;
        section.UseLongitudinalWater = true;
        section.SiltThickness = 0.25; section.SiltLeft = -2; section.SiltRight = 3;
        var restored = ProjectFile.Clone(new ProfileProject { Sections = new List<CrossSection> { section } }).Sections[0];
        Assert.Equal(SectionPointRole.LeftBank, restored.Points[0].Role);
        Assert.True(restored.Points[0].UseLongitudinalElevation);
        Assert.True(restored.UsePointDistances);
        Assert.True(restored.UseLongitudinalWater);
        Assert.Equal(SectionDesignMode.Points, restored.DesignMode);
        Assert.Equal(1, restored.ProjectAnchorIndex);
        Assert.Equal(3.25, restored.Points[1].DistanceFromPrevious);
        Assert.Equal(0.25, restored.SiltThickness);
        Assert.Equal(-2, restored.SiltLeft);
        Assert.Equal(3, restored.SiltRight);
    }
    [Fact]
    public void LinkedSectionCannotUseCachedSeedHeightsWithoutCurrentLongitudinalData()
    {
        var section = new CrossSection
        {
            Station = 0, UseLongitudinalProjectBottom = true,
            ProjectBottom = 8, BottomWidth = 2, LeftSlope = 1, RightSlope = 1,
            Points = new List<SectionPoint> { new() { Offset = -5, Existing = 10 }, new() { Offset = 5, Existing = 10 } }
        };
        Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.ResolveSection(section));
        var result = Earthworks.Calculate(section);
        Assert.False(result.IsValid);
        Assert.Null(result.CutArea);
        Assert.Contains("продольного профиля", result.Status);
        section.UseLongitudinalProjectBottom = false;
        Assert.True(Earthworks.Calculate(section).IsValid);
    }

    private static CrossSection EmptyUiSection() => new CrossSection
    {
        Name = "Поперечник 1", UsePointDistances = true, DesignMode = SectionDesignMode.None,
        Points = new List<SectionPoint>
        {
            new() { Role = SectionPointRole.LeftBank, DistanceFromPrevious = 0 },
            new() { Role = SectionPointRole.Bottom },
            new() { Role = SectionPointRole.RightBank }
        }
    };

    private static ProfileProject SectionsAround(CrossSection middle) => new ProfileProject
    {
        Sections = new List<CrossSection>
        {
            new() { Station = 0, Points = new List<SectionPoint> { new() { Offset = 0, Existing = 10, Design = 9 }, new() { Offset = 10, Existing = 10, Design = 9 } } },
            middle,
            new() { Station = 100, Points = new List<SectionPoint> { new() { Offset = 0, Existing = 10, Design = 9 }, new() { Offset = 10, Existing = 10, Design = 9 } } }
        }
    };

    [Fact]
    public void UntouchedThreeRoleUiPlaceholderDoesNotBlockVolumesOrWarnOnLongOnlyProject()
    {
        var placeholder = EmptyUiSection();
        var complete = Earthworks.CalculateVolumes(SectionsAround(placeholder));
        Assert.Equal(1000, complete.TotalCutVolume);
        Assert.Empty(complete.Warnings);
        var empty = Earthworks.CalculateVolumes(new ProfileProject { Sections = new List<CrossSection> { placeholder } });
        Assert.Empty(empty.Stations);
        Assert.Empty(empty.Intervals);
        Assert.Empty(empty.Warnings);
        Assert.Null(empty.TotalCutVolume);
        Assert.Empty(Earthworks.CalculateVolumes(new ProfileProject()).Warnings);
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("zeroInterval")]
    [InlineData("offset")]
    [InlineData("height")]
    [InlineData("anchor")]
    [InlineData("linked")]
    public void UnsitedUiDraftWithActualInputWarnsAndPreservesCalculatedSubtotal(string input)
    {
        var section = EmptyUiSection();
        if (input == "distance") section.Points[1].DistanceFromPrevious = 5;
        if (input == "zeroInterval") section.Points[1].DistanceFromPrevious = 0;
        if (input == "offset") section.Points[1].Offset = 5;
        if (input == "height") section.Points[1].Existing = 0;
        if (input == "anchor") section.ProjectAnchorIndex = 1;
        if (input == "linked") section.UseLongitudinalWater = true;
        var result = Earthworks.CalculateVolumes(SectionsAround(section));
        Assert.Equal(1000, result.TotalCutVolume);
        Assert.Equal(0, result.TotalFillVolume);
        Assert.Equal(0, result.TotalSiltVolume);
        Assert.True(result.HasUnassignedSections);
        Assert.NotEmpty(result.Warnings);
    }

}
