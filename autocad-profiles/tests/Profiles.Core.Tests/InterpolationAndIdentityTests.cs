using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class InterpolationAndIdentityTests
{
    [Fact]
    public void UnequalStationSpacingUsesDistancesAndPreservesRawNulls()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = 100, ProjectBottom = 10 },
            new() { Station = 125 }, new() { Station = 190 },
            new() { Station = 200, ProjectBottom = 14 }
        };
        var resolved = ProfileInterpolation.ResolveLongitudinal(source);
        Assert.Equal(11, resolved[1].ProjectBottom);
        Assert.Equal(13.6, resolved[2].ProjectBottom!.Value, 10);
        Assert.Null(source[1].ProjectBottom);
        Assert.Null(source[2].ProjectBottom);
        source[3].ProjectBottom = 18;
        Assert.Equal(12, ProfileInterpolation.ResolveLongitudinal(source)[1].ProjectBottom);
    }

    [Fact]
    public void EveryElevationColumnHasIndependentAnchorsWhileDimensionsStayRaw()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = 0, Ground = 20, LeftBank = 16, RightBank = 17, ExistingBottom = 10, Water = 12, ProjectBottom = 9, ProjectGround = 30, PipeInvert = 25, PipeDescription = "ПЭ 160", Foundation = "Песок", PointName = "К1", PeatDepth = 0, BottomWidth = 2 },
            new() { Station = 25, LeftBank = 99 },
            new() { Station = 100, Ground = 24, LeftBank = 20, RightBank = 21, ExistingBottom = 14, Water = 16, ProjectBottom = 13, ProjectGround = 34, PipeInvert = 29, BottomWidth = 4 }
        };
        var middle = ProfileInterpolation.ResolveLongitudinal(source)[1];
        Assert.Equal(21, middle.Ground);
        Assert.Equal(99, middle.LeftBank); // User input wins even if it differs from adjacent values.
        Assert.Equal(18, middle.RightBank);
        Assert.Equal(11, middle.ExistingBottom);
        Assert.Equal(13, middle.Water);
        Assert.Equal(10, middle.ProjectBottom);
        Assert.Null(middle.BottomWidth);
        Assert.Null(middle.LeftSlope);
        Assert.Equal(31, middle.ProjectGround);
        Assert.Equal(26, middle.PipeInvert);
        Assert.Empty(middle.PipeDescription);
        Assert.Empty(middle.Foundation);
        Assert.Empty(middle.PointName);
        Assert.Null(middle.PeatDepth);
        var exact = ProfileInterpolation.SampleLongitudinal(source, 0);
        Assert.Equal("ПЭ 160", exact.PipeDescription);
        Assert.Equal("Песок", exact.Foundation);
        Assert.Equal("К1", exact.PointName);
        Assert.Equal(0, exact.PeatDepth);
    }

    [Fact]
    public void NoExtrapolationAndZeroIsAnAnchor()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = -1 }, new() { Station = 0, Ground = 0 },
            new() { Station = 2 }, new() { Station = 8, Ground = 4 }, new() { Station = 9 }
        };
        var resolved = ProfileInterpolation.ResolveLongitudinal(source);
        Assert.Null(resolved[0].Ground);
        Assert.Equal(0, resolved[1].Ground);
        Assert.Equal(1, resolved[2].Ground);
        Assert.Null(resolved[4].Ground);
        Assert.Null(ProfileInterpolation.SampleLongitudinal(source, -2).Ground);
        Assert.Null(ProfileInterpolation.SampleLongitudinal(source, 10).Ground);
    }

    [Fact]
    public void SamplingWorksBetweenRowsAndPreservesDimensionsOnlyAtExactStation()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = 10, Water = 100, BottomWidth = 2, LeftSlope = 1.5, RightSlope = 2 },
            new() { Station = 50, Water = 104, BottomWidth = 4 }
        };
        Assert.Equal(101, ProfileInterpolation.SampleLongitudinal(source, 20).Water);
        Assert.Null(ProfileInterpolation.SampleLongitudinal(source, 20).BottomWidth);
        Assert.Equal(2, ProfileInterpolation.SampleLongitudinal(source, 10).BottomWidth);
        Assert.Equal(1.5, ProfileInterpolation.SampleLongitudinal(source, 10).LeftSlope);
    }

    [Fact]
    public void StationSeedCarriesElevationsWithoutInventingCrossDistances()
    {
        var source = new List<LongitudinalPoint>
        {
            new() { Station = 0, LeftBank = 12, RightBank = 14, ExistingBottom = 8, Water = 9, ProjectBottom = 7 },
            new() { Station = 30, BottomWidth = 2, LeftSlope = 1.5, RightSlope = 2 },
            new() { Station = 120, LeftBank = 16, RightBank = 18, ExistingBottom = 12, Water = 13, ProjectBottom = 11 }
        };
        var section = ProfileInterpolation.CreateSection(source, 30);
        Assert.Equal(30, section.Station);
        Assert.Equal(13, section.LeftBank);
        Assert.Equal(15, section.RightBank);
        Assert.Equal(9, section.ExistingBottom);
        Assert.Equal(10, section.Water);
        Assert.Equal(8, section.ProjectBottom);
        Assert.Equal(2, section.BottomWidth);
        Assert.Equal(1.5, section.LeftSlope);
        Assert.Equal(3, section.Points.Count);
        Assert.True(section.UsePointDistances);
        Assert.Equal(0, section.Points[0].DistanceFromPrevious);
        Assert.Null(section.Points[1].DistanceFromPrevious);
        Assert.Null(section.Points[2].DistanceFromPrevious);
        Assert.Contains("ПК", section.Name);
    }

    [Fact]
    public void CrossInterpolationUsesOffsetsAndDoesNotChangeGridValues()
    {
        var source = new List<SectionPoint>
        {
            new() { Offset = -3, Existing = 10, Design = 5 },
            new() { Offset = 0 }, new() { Offset = 7, Existing = 20, Design = 15 }
        };
        Assert.Equal(13, ProfileInterpolation.SectionElevationAt(source, 0));
        Assert.Equal(8, ProfileInterpolation.SectionElevationAt(source, 0, design: true));
        Assert.Null(source[1].Existing);
        Assert.Null(ProfileInterpolation.SectionElevationAt(source, -4));
        Assert.Null(ProfileInterpolation.SectionElevationAt(source, 8));
        source[1].Existing = 42;
        Assert.Equal(42, ProfileInterpolation.SectionElevationAt(source, 0));
    }

    [Fact]
    public void CenterUsesLowestPointOrFlatBottomMidpoint()
    {
        var points = new List<SectionPoint>
        {
            new() { Offset = -10, Existing = 20 }, new() { Offset = -3, Existing = 8 },
            new() { Offset = 1, Existing = 8 }, new() { Offset = 10, Existing = 18 }
        };
        Assert.Equal(-1, ProfileInterpolation.FindCenterOffset(points));
        points[1].Existing = 9;
        Assert.Equal(1, ProfileInterpolation.FindCenterOffset(points));
    }

    [Fact]
    public void SeparateEqualLowsDoNotCreateInventedFlatBottomAcrossGapOrHill()
    {
        var points = new List<SectionPoint>
        {
            new() { Offset = -10, Existing = 20 }, new() { Offset = -7, Existing = 8 },
            new() { Offset = 0, Existing = 15 }, new() { Offset = 2, Existing = 8 },
            new() { Offset = 10, Existing = 20 }
        };
        Assert.Equal(2, ProfileInterpolation.FindCenterOffset(points));
        points[2].Existing = null;
        Assert.Equal(2, ProfileInterpolation.FindCenterOffset(points));
        Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.FindCenterOffset(new List<SectionPoint> { new() { Offset = 0 } }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void InvalidStationOrderOrCoordinateIsRejected(double second)
    {
        var points = new List<LongitudinalPoint> { new() { Station = 0, Ground = 1 }, new() { Station = second, Ground = 2 } };
        Assert.Throws<ProfileValidationException>(() => ProfileInterpolation.ResolveLongitudinal(points));
    }

    [Fact]
    public void IdentityAndPendingSeedDataRoundtripWithoutPromotingDerivedValues()
    {
        var project = new ProfileProject { Name = "Канал № 3", Template = ProfileTemplate.Nvk };
        project.Longitudinal.AddRange(new[]
        {
            new LongitudinalPoint { Station = 0, ExistingBottom = 10 },
            new LongitudinalPoint { Station = 25 },
            new LongitudinalPoint { Station = 100, ExistingBottom = 14 }
        });
        project.Sections.Add(ProfileInterpolation.CreateSection(project.Longitudinal, 25));
        var resolved = ProfileInterpolation.ResolveLongitudinal(project);
        Assert.Equal(11, resolved.Longitudinal[1].ExistingBottom);
        var restored = ProjectFile.Deserialize(ProjectFile.Serialize(project));
        Assert.Equal(5, restored.SchemaVersion);
        Assert.Equal(project.ProjectId, restored.ProjectId);
        Assert.Equal(project.Name, restored.Name);
        Assert.Equal(ProfileTemplate.Nvk, restored.Template);
        Assert.Null(restored.Longitudinal[1].ExistingBottom);
        Assert.Equal(11, restored.Sections[0].ExistingBottom);
        Assert.Equal(3, restored.Sections[0].Points.Count);
        Assert.Null(restored.Sections[0].Points[1].DistanceFromPrevious);
        var clone = ProjectFile.Clone(restored);
        clone.Longitudinal[0].ExistingBottom = 20;
        Assert.Equal(10, restored.Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void LegacyFullSchemaMigratesOnceAndRetainsMissingValues()
    {
        var source = SampleProjects.CrossSections();
        source.Longitudinal.Clear(); // This legacy fixture has one independent longitudinal point.
        source.Longitudinal.Add(new LongitudinalPoint { Station = 0, Ground = 10, ExistingBottom = 8 });
        var root = JsonNode.Parse(ProjectFile.Serialize(source))!.AsObject();
        root["SchemaVersion"] = 1;
        SchemaTestDocuments.RemoveVersionFiveFields(root);
        root["Settings"]!.AsObject().Remove("CrossHorizontalScale");
        root["Settings"]!.AsObject().Remove("CrossVerticalScale");
        root.Remove("ProjectId");
        root.Remove("Name");
        root.Remove("Template");
        foreach (var item in root["Longitudinal"]!.AsArray())
            foreach (string name in new[] { "ProjectGround", "PipeInvert", "PipeDescription", "Foundation", "PointName", "PeatDepth" })
                item!.AsObject().Remove(name);
        foreach (var item in root["Sections"]!.AsArray())
        {
            var section = item!.AsObject();
            section.Remove("LeftBank"); section.Remove("RightBank"); section.Remove("ExistingBottom");
            foreach (string name in new[] { "DesignMode", "UsePointDistances", "ProjectAnchorIndex", "UseLongitudinalProjectBottom", "UseLongitudinalWater", "SiltThickness", "SiltLeft", "SiltRight" }) section.Remove(name);
            foreach (var node in section["Points"]!.AsArray())
                foreach (string name in new[] { "Role", "DistanceFromPrevious", "UseLongitudinalElevation" }) node!.AsObject().Remove(name);
        }
        var imported = ProjectFile.Deserialize(root.ToJsonString());
        Assert.Equal(5, imported.SchemaVersion);
        Assert.Equal(ProfileTemplate.Reconstruction, imported.Template);
        Assert.Null(imported.Longitudinal[0].ProjectGround);
        Assert.Null(imported.Longitudinal[0].PipeInvert);
        Assert.Empty(imported.Longitudinal[0].PipeDescription);
        Assert.True(Guid.TryParseExact(imported.ProjectId, "N", out _));
        Assert.Equal(source.Sections.Count, imported.Sections.Count);
        Assert.Null(imported.Sections[0].LeftBank);
        Assert.Equal(source.Sections[0].Points.Count, imported.Sections[0].Points.Count);
        Assert.Equal(imported.ProjectId, ProjectFile.Clone(imported).ProjectId);
        root["Longitudinal"]![0]!["ExistingBottom"] = null;
        Assert.Equal(ProfileTemplate.NewChannel, ProjectFile.Deserialize(root.ToJsonString()).Template);
        root["Settings"]!.AsObject().Remove("HorizontalScale");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(root.ToJsonString()));
    }

    [Fact]
    public void CurrentSchemaRequiresValidIdentityAndName()
    {
        var project = new ProfileProject { ProjectId = "not-a-project-id" };
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(project));
        project.ProjectId = Guid.NewGuid().ToString("D");
        Assert.Equal(Guid.Parse(project.ProjectId).ToString("N"), ProjectFile.Clone(project).ProjectId);
        project.Name = " ";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(project));
        var json = JsonNode.Parse(ProjectFile.Serialize(new ProfileProject()))!.AsObject();
        json.Remove("Name");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(json.ToJsonString()));
    }
    [Fact]
    public void NvkDataAndZeroPeatDepthRoundtripAndRejectInvalidInput()
    {
        var project = new ProfileProject { Template = ProfileTemplate.Nvk };
        project.Longitudinal.Add(new LongitudinalPoint
        {
            Station = 125, Ground = 10, ProjectGround = 11, PipeInvert = 8,
            PipeDescription = "ПЭ100 160×9,5", Foundation = "Песок 100 мм", PointName = "КК-1", PeatDepth = 0
        });
        var restored = ProjectFile.Clone(project);
        var point = restored.Longitudinal[0];
        Assert.Equal(ProfileTemplate.Nvk, restored.Template);
        Assert.Equal(11, point.ProjectGround);
        Assert.Equal(8, point.PipeInvert);
        Assert.Equal("ПЭ100 160×9,5", point.PipeDescription);
        Assert.Equal("Песок 100 мм", point.Foundation);
        Assert.Equal("КК-1", point.PointName);
        Assert.Equal(0, point.PeatDepth);
        project.Longitudinal[0].PeatDepth = -1;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(project));
        project.Longitudinal[0].PeatDepth = null;
        project.Longitudinal[0].PointName = new string('A', 101);
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(project));
    }

}
