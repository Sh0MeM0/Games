using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class SectionIdentityAndRoleTests
{
    [Fact]
    public void VersionFourMigratesRepeatablyToOriginalProfileAndStableSectionIdentities()
    {
        var source = Project();
        source.ProjectId = Guid.Parse(source.ProjectId).ToString("D");
        source.Sections[0].Points[0].Label = "Новая подпись";
        var document = JsonNode.Parse(ProjectFile.Serialize(source))!.AsObject();
        SchemaTestDocuments.RemoveVersionFiveFields(document);
        document["SchemaVersion"] = 4;
        var first = ProjectFile.Deserialize(document.ToJsonString());
        var second = ProjectFile.Deserialize(document.ToJsonString());
        Assert.Equal(5, first.SchemaVersion);
        Assert.Equal(Guid.Parse(source.ProjectId).ToString("N"), first.ProfileId);
        Assert.Equal(first.ProjectId, first.ProfileId);
        Assert.Equal("Профиль 1", first.ProfileName);
        Assert.Equal(0, first.ActiveProfileIndex);
        Assert.Empty(first.AdditionalProfiles);
        Assert.Equal(first.Sections[0].SectionId, second.Sections[0].SectionId);
        Assert.True(Guid.TryParseExact(first.Sections[0].SectionId, "N", out _));
        Assert.All(first.Sections[0].Points, point => Assert.Empty(point.Label));
        var roundtrip = ProjectFile.Clone(first);
        Assert.Equal(first.ProfileId, roundtrip.ProfileId);
        Assert.Equal(first.Sections[0].SectionId, roundtrip.Sections[0].SectionId);
        Assert.Equal(first.Sections[0].Points[1].Existing, roundtrip.Sections[0].Points[1].Existing);
    }

    [Fact]
    public void IdentifiersAndLabelsSurviveCloneWithoutAliasing()
    {
        var project = Project();
        project.Sections[0].Points[0].Label = "Левый берег у дороги";
        var copy = ProfileInterpolation.CloneSection(project.Sections[0]);
        Assert.Equal(project.Sections[0].SectionId, copy.SectionId);
        Assert.Equal("Левый берег у дороги", copy.Points[0].Label);
        copy.Points[0].Label = "Другая подпись";
        Assert.Equal("Левый берег у дороги", project.Sections[0].Points[0].Label);
        var reopened = ProjectFile.Clone(project);
        Assert.Equal(project.ProfileId, reopened.ProfileId);
        Assert.Equal(project.Sections[0].SectionId, reopened.Sections[0].SectionId);
        Assert.Equal("Левый берег у дороги", reopened.Sections[0].Points[0].Label);
    }

    [Fact]
    public void DuplicateIdentityRequiresExplicitNewCopyIdentity()
    {
        var source = Project();
        var duplicate = ProfileInterpolation.CloneSection(source.Sections[0]);
        source.Sections.Add(duplicate);
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(source));
        duplicate.SectionId = Guid.NewGuid().ToString("N");
        Assert.Equal(2, ProjectFile.Clone(source).Sections.Count);
        duplicate.SectionId = Guid.Empty.ToString("N");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(source));
    }

    [Fact]
    public void NewMetadataFieldsAreStrictAndCannotAppearInOldSchema()
    {
        var source = Project();
        var current = JsonNode.Parse(ProjectFile.Serialize(source))!.AsObject();
        current["Sections"]![0]!.AsObject().Remove("SectionId");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(current.ToJsonString()));
        var old = JsonNode.Parse(ProjectFile.Serialize(source))!.AsObject();
        old["SchemaVersion"] = 4;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(old.ToJsonString()));
        SchemaTestDocuments.RemoveVersionFiveFields(old);
        old["Sections"]![0]!["Points"]![0]!["Label"] = "smuggled";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(old.ToJsonString()));
        source.Sections[0].Points[0].Label = new string('a', 101);
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(source));
        source.Sections[0].Points[0].Label = "Две\nстроки";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(source));
    }

    [Fact]
    public void RoleReassignmentUsesSelectedSurveyPointAndLaterEditsStayBidirectional()
    {
        var source = Project();
        var before = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Points[0].Role = SectionPointRole.Regular;
        edited.Points[3].Role = SectionPointRole.LeftBank;
        edited.Points[3].Label = "Берег под кустом";
        var result = ProjectSynchronization.CommitSection(source, 0, before, edited);
        Assert.Equal(15.123456789, result.Longitudinal[0].LeftBank);
        Assert.Equal(10, result.Sections[0].Points[0].Existing);
        Assert.False(result.Sections[0].Points[0].UseLongitudinalElevation);
        Assert.Equal(15.123456789, result.Sections[0].LeftBank);
        Assert.True(result.Sections[0].Points[3].UseLongitudinalElevation);
        var fromLong = ProjectSynchronization.SetSharedValue(result, 0, SharedProfileField.LeftBank, 16.987654321);
        Assert.Equal(16.987654321, fromLong.Sections[0].Points[3].Existing);
        Assert.Equal(10, fromLong.Sections[0].Points[0].Existing);
        Assert.Equal("Берег под кустом", fromLong.Sections[0].Points[3].Label);
        Assert.Equal(3.123456789, fromLong.Sections[0].Points[3].DistanceFromPrevious);
        Assert.Equal(10, source.Longitudinal[0].LeftBank);
    }

    [Fact]
    public void ReassigningRoleToSameNumericDerivedElevationIsStillExplicitStationInput()
    {
        var source = Project();
        source.Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, LeftBank = 10 }, new() { Station = 50 }, new() { Station = 100, LeftBank = 10 }
        };
        var section = source.Sections[0];
        section.Station = 50;
        section.Points[3].Existing = 10;
        var previous = ProfileInterpolation.CloneSection(section);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Points[0].Role = SectionPointRole.Regular;
        edited.Points[3].Role = SectionPointRole.LeftBank;
        var result = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        Assert.Equal(10, result.Longitudinal[1].LeftBank);
        Assert.Null(source.Longitudinal[1].LeftBank);
    }

    [Fact]
    public void EmptyNewRoleLinksExistingStationValueWithoutClearingIt()
    {
        var source = Project();
        source.Sections[0].Points[3].Existing = null;
        var previous = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(previous);
        edited.Points[0].Role = SectionPointRole.Regular;
        edited.Points[3].Role = SectionPointRole.LeftBank;
        var result = ProjectSynchronization.CommitSection(source, 0, previous, edited);
        Assert.Equal(10, result.Longitudinal[0].LeftBank);
        Assert.Equal(10, result.Sections[0].Points[3].Existing);
        Assert.True(result.Sections[0].Points[3].UseLongitudinalElevation);
    }

    [Fact]
    public void ExplicitDuplicateRoleAssignmentAndWrongSectionIdentityRejectAtomically()
    {
        var source = Project();
        string original = ProjectFile.Serialize(source);
        var before = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Points[3].Role = SectionPointRole.LeftBank;
        Assert.Throws<ProfileValidationException>(() => ProjectSynchronization.CommitSection(source, 0, before, edited));
        edited = ProfileInterpolation.CloneSection(before);
        edited.SectionId = Guid.NewGuid().ToString("N");
        Assert.Throws<ProfileValidationException>(() => ProjectSynchronization.CommitSection(source, 0, before, edited));
        Assert.Equal(original, ProjectFile.Serialize(source));
    }

    [Fact]
    public void LabelOnlyEditDoesNotPromoteDerivedElevationToRawAnchor()
    {
        var source = Project();
        source.Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, LeftBank = 10 }, new() { Station = 50 }, new() { Station = 100, LeftBank = 12 }
        };
        source.Sections[0].Station = 50;
        source = ProjectSynchronization.RefreshFromLongitudinal(source);
        var before = ProfileInterpolation.CloneSection(source.Sections[0]);
        var edited = ProfileInterpolation.CloneSection(before);
        edited.Points[0].Label = "Верх берега";
        var result = ProjectSynchronization.CommitSection(source, 0, before, edited);
        Assert.Null(result.Longitudinal[1].LeftBank);
        Assert.Equal(11, result.Sections[0].Points[0].Existing);
        Assert.Equal("Верх берега", result.Sections[0].Points[0].Label);
        Assert.Equal(source.Sections[0].SectionId, result.Sections[0].SectionId);
    }

    [Fact]
    public void InactiveProfilesAreValidatedAndIdentityScopeIsGlobal()
    {
        var project = Project();
        var other = ProfileCollection.Add(project, "Второй");
        other.Sections.Add(ProfileInterpolation.CloneSection(other.AdditionalProfiles[0].Sections[0]));
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(other));
        other.Sections[0].SectionId = Guid.NewGuid().ToString("N");
        Assert.NotEmpty(ProjectFile.Serialize(other));
        other.AdditionalProfiles[0].Longitudinal[0].Water = double.NaN;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(other));
    }

    private static ProfileProject Project()
    {
        var result = new ProfileProject();
        result.Longitudinal.Add(new LongitudinalPoint { Station = 0, LeftBank = 10, ExistingBottom = 8, RightBank = 12 });
        var section = ProfileInterpolation.CreateSection(result.Longitudinal, 0);
        section.Points.Add(new SectionPoint { Offset = 11, DistanceFromPrevious = 3.123456789, Existing = 15.123456789 });
        result.Sections.Add(section);
        return result;
    }
}
