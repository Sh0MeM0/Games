using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Profiles.AutoCAD;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class OutputMetadataTests
{
    private const string Project = "11111111111111111111111111111111";
    private const string Profile = "22222222222222222222222222222222";
    private const string OtherProfile = "33333333333333333333333333333333";
    private const string Section = "44444444444444444444444444444444";
    private const string OtherSection = "55555555555555555555555555555555";

    [Theory]
    [InlineData(ProfileKind.Longitudinal, null, null, 34)]
    [InlineData(ProfileKind.CrossSection, null, null, 34)]
    [InlineData(ProfileKind.Longitudinal, Profile, null, 67)]
    [InlineData(ProfileKind.CrossSection, Profile, Section, 100)]
    public void LegacyAndIndependentAddressesRoundtripWithoutAmbiguousSegments(ProfileKind kind, string profileId, string sectionId, int length)
    {
        string key = OutputMetadata.OutputKey(Project, kind, profileId, sectionId);
        Assert.Equal(length, key.Length);
        OutputMetadata.ParseOutputKey(key, out string project, out ProfileKind parsedKind, out string profile, out string section);
        Assert.Equal(Project, project);
        Assert.Equal(kind, parsedKind);
        Assert.Equal(profileId, profile);
        Assert.Equal(sectionId, section);
    }

    [Fact]
    public void DeletingAProjectSelectsEveryOutputRecordOfThatProjectOnly()
    {
        const string otherProject = "66666666666666666666666666666666";
        var own = new[]
        {
            OutputMetadata.OutputKey(Project, ProfileKind.Longitudinal),
            OutputMetadata.OutputKey(Project, ProfileKind.CrossSection),
            OutputMetadata.OutputKey(Project, ProfileKind.Longitudinal, Profile),
            OutputMetadata.OutputKey(Project, ProfileKind.CrossSection, Profile, Section)
        };
        foreach (string key in own)
        {
            Assert.True(OutputMetadata.BelongsToProject(key, Project));
            Assert.True(OutputMetadata.BelongsToProject(key, Project.ToUpperInvariant()));
            Assert.False(OutputMetadata.BelongsToProject(key, otherProject));
        }
        Assert.False(OutputMetadata.BelongsToProject(OutputMetadata.OutputKey(otherProject, ProfileKind.CrossSection, Profile, Section), Project));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.BelongsToProject("HEADER", Project));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.BelongsToProject(own[0], "not-a-project"));
    }

    [Fact]
    public void SelectedCrossScopeCannotEraseLegacyBatchAnotherSectionOrAnotherProfile()
    {
        var selected = Output(ProfileKind.CrossSection, Profile, Section);
        var otherSection = Output(ProfileKind.CrossSection, Profile, OtherSection);
        var otherProfile = Output(ProfileKind.CrossSection, OtherProfile, Section);
        var legacy = Legacy(ProfileKind.CrossSection);
        Assert.True(InScope(selected, Profile, Section));
        Assert.False(InScope(otherSection, Profile, Section));
        Assert.False(InScope(otherProfile, Profile, Section));
        Assert.False(InScope(legacy, Project, Section));
        Assert.False(InScope(legacy, Project, null));
        Assert.True(InScope(legacy, Project, null, allSections: true));
        Assert.False(InScope(legacy, Profile, null, allSections: true));
        Assert.True(InScope(otherSection, Profile, Section, allSections: true));
        Assert.False(InScope(otherProfile, Profile, Section, allSections: true));
    }

    [Fact]
    public void AllLongitudinalScopeNeverTouchesCrossGeometryOrAnotherProject()
    {
        var own = Output(ProfileKind.Longitudinal, Profile);
        var another = Output(ProfileKind.Longitudinal, OtherProfile);
        var cross = Output(ProfileKind.CrossSection, Profile, Section);
        var foreign = Output(ProfileKind.Longitudinal, Profile); foreign.ProjectId = OtherSection;
        Assert.True(OutputMetadata.InScope(own, Project, Profile, ProfileKind.Longitudinal, null, false, false));
        Assert.False(OutputMetadata.InScope(another, Project, Profile, ProfileKind.Longitudinal, null, false, false));
        Assert.True(OutputMetadata.InScope(another, Project, Profile, ProfileKind.Longitudinal, null, false, true));
        Assert.False(OutputMetadata.InScope(cross, Project, Profile, ProfileKind.Longitudinal, null, false, true));
        Assert.False(OutputMetadata.InScope(foreign, Project, Profile, ProfileKind.Longitudinal, null, false, true));
    }

    [Fact]
    public void LegacyOwnerIsOriginalProjectIdentityEvenAfterActiveProfileChanges()
    {
        var legacy = Legacy(ProfileKind.Longitudinal);
        Assert.Equal(Project, OutputMetadata.OutputProfileId(legacy));
        Assert.True(OutputMetadata.InScope(legacy, Project, Project, ProfileKind.Longitudinal, null, false, false));
        Assert.False(OutputMetadata.InScope(legacy, Project, Profile, ProfileKind.Longitudinal, null, false, false));
        string json = JsonConvert.SerializeObject(legacy);
        Assert.DoesNotContain("ProfileId", json);
        Assert.DoesNotContain("SectionId", json);
        var restored = JsonConvert.DeserializeObject<StoredProfileOutput>(json)!;
        OutputMetadata.ValidateOutput(restored, Project, ProfileKind.Longitudinal);
    }

    [Fact]
    public void RebuildSelectsEachSavedPlacementAndCannotSubstituteLegacyBatchOrAnotherProfile()
    {
        var first = Output(ProfileKind.CrossSection, Project, Section);
        var second = Output(ProfileKind.CrossSection, Project, OtherSection);
        first.Transform[3] = 123.4567890123;
        second.Transform[3] = -987.6543210987;
        second.Transform[7] = 432.123456789;
        var sameSectionOtherProfile = Output(ProfileKind.CrossSection, OtherProfile, Section);
        var legacy = Legacy(ProfileKind.CrossSection);
        var records = new[] { legacy, sameSectionOtherProfile, second, first };

        Assert.Same(first, OutputMetadata.FindPrevious(records, Project, ProfileKind.CrossSection, Project, Section));
        Assert.Same(second, OutputMetadata.FindPrevious(records, Project, ProfileKind.CrossSection, Project, OtherSection));
        Assert.Equal(123.4567890123, first.Transform[3]);
        Assert.Equal(-987.6543210987, second.Transform[3]);
        Assert.Null(OutputMetadata.FindPrevious(new[] { legacy }, Project, ProfileKind.CrossSection, Project, Section));
        Assert.Null(OutputMetadata.FindPrevious(records, Project, ProfileKind.CrossSection, Profile, Section));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RigidSavedPlacementRetainsTranslationAndTiltWithoutNormalization(int version)
    {
        var output = version == 1 ? Legacy(ProfileKind.CrossSection) : Output(ProfileKind.CrossSection, Profile, Section);
        output.Transform = new[] { 0.0, 0, 1, 12345.678901234, 1, 0, 0, -23456.789012345, 0, 1, 0, 34567.890123456, 0, 0, 0, 1 };
        var original = output.Transform.ToArray();
        OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, output.ProfileId, output.SectionId);
        Assert.Equal(original, output.Transform);
    }

    [Fact]
    public void WrongIdentityDuplicateHandlesAndNonRigidPlacementAreRejected()
    {
        var output = Output(ProfileKind.CrossSection, Profile, Section);
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, OtherProfile, Section));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, Profile, OtherSection));
        output.Handles.Add("a");
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, Profile, Section));
        output.Handles.RemoveAt(output.Handles.Count - 1);
        output.Transform[0] = 2;
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, Profile, Section));
        output.Transform[0] = double.NaN;
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, Profile, Section));
    }

    [Fact]
    public void CrossWithoutSectionLongWithSectionAndUnknownMetadataVersionsAreRejected()
    {
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.OutputKey(Project, ProfileKind.CrossSection, Profile));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.OutputKey(Project, ProfileKind.Longitudinal, Profile, Section));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.OutputKey(Project, (ProfileKind)99));
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ParseOutputKey(Project + "_C_" + Section, out _, out _, out _, out _));
        var output = Output(ProfileKind.CrossSection, Profile, Section);
        output.Version = 3;
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection, Profile, Section));
        output.Version = 1;
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateOutput(output, Project, ProfileKind.CrossSection));
    }

    [Fact]
    public void StoredJsonDoesNotCoerceNumericStringsOrNumericIdentityFields()
    {
        var value = JObject.FromObject(Output(ProfileKind.CrossSection, Profile, Section));
        OutputMetadata.ValidateJson(value);
        value["Version"] = "2";
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateJson(value));
        value["Version"] = 2;
        value["Transform"]![0] = "1";
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateJson(value));
        value["Transform"]![0] = 1;
        value["Handles"]![0] = 10;
        Assert.Throws<ProfileValidationException>(() => OutputMetadata.ValidateJson(value));
    }

    private static bool InScope(StoredProfileOutput output, string profileId, string sectionId, bool allSections = false) =>
        OutputMetadata.InScope(output, Project, profileId, ProfileKind.CrossSection, sectionId, allSections, false);

    private static StoredProfileOutput Legacy(ProfileKind kind)
    {
        var output = Output(kind, null);
        output.Version = 1;
        return output;
    }

    private static StoredProfileOutput Output(ProfileKind kind, string profileId, string sectionId = null) => new()
    {
        Version = 2, ProjectId = Project, ProfileId = profileId, SectionId = sectionId,
        Kind = kind == ProfileKind.Longitudinal ? "L" : "C", DrawingId = OtherProfile,
        GenerationId = OtherSection, Fingerprint = "66666666-6666-6666-6666-666666666666", SpaceHandle = "F",
        Handles = new List<string> { "A", "B" },
        Transform = new[] { 1.0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
    };
}
