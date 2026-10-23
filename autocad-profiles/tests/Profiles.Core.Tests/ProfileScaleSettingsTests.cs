using System;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProfileScaleSettingsTests
{
    [Fact]
    public void EditingLongitudinalMaterializesLegacyCrossFallbackBeforeChangingIt()
    {
        var settings = new ProfileSettings { HorizontalScale = 250, VerticalScale = 25 };
        ProfileScales.Set(settings, ProfileKind.Longitudinal, 1200, 150);
        Assert.Equal(1200, ProfileScales.GetHorizontal(settings, ProfileKind.Longitudinal));
        Assert.Equal(150, ProfileScales.GetVertical(settings, ProfileKind.Longitudinal));
        Assert.Equal(250, ProfileScales.GetHorizontal(settings, ProfileKind.CrossSection));
        Assert.Equal(25, ProfileScales.GetVertical(settings, ProfileKind.CrossSection));
        ProfileScales.Set(settings, ProfileKind.CrossSection, 100, 50);
        Assert.Equal(1200, settings.HorizontalScale);
        Assert.Equal(150, settings.VerticalScale);
    }

    [Fact]
    public void InvalidEditLeavesBothStoredPairsUntouched()
    {
        var settings = new ProfileSettings { HorizontalScale = 800, VerticalScale = 80 };
        Assert.Throws<ProfileValidationException>(() => ProfileScales.Set(settings, ProfileKind.Longitudinal, 1200, double.NaN));
        Assert.Equal(800, settings.HorizontalScale);
        Assert.Equal(80, settings.VerticalScale);
        Assert.Null(settings.CrossHorizontalScale);
        Assert.Null(settings.CrossVerticalScale);
        Assert.Throws<ProfileValidationException>(() => ProfileScales.Set(settings, ProfileKind.CrossSection, 0, 100));
        Assert.Null(settings.CrossHorizontalScale);
    }

    [Fact]
    public void CurrentSchemaRequiresBothCrossPropertiesButAcceptsNullLegacyPair()
    {
        var document = JsonNode.Parse(ProjectFile.Serialize(new ProfileProject()))!.AsObject();
        Assert.Equal(4, document["SchemaVersion"]!.GetValue<int>());
        Assert.Null(ProjectFile.Deserialize(document.ToJsonString()).Settings.CrossHorizontalScale);
        document["Settings"]!.AsObject().Remove("CrossVerticalScale");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Theory]
    [InlineData(null, 100.0)]
    [InlineData(100.0, null)]
    [InlineData(0.0, 100.0)]
    [InlineData(100.0, 1000001.0)]
    public void IncompleteOrInvalidInactiveCrossScalesCannotBeSaved(double? horizontal, double? vertical)
    {
        var project = new ProfileProject { Kind = ProfileKind.Longitudinal };
        project.Settings.CrossHorizontalScale = horizontal;
        project.Settings.CrossVerticalScale = vertical;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Serialize(project));
    }

    [Fact]
    public void OldSchemaCannotSmuggleNewScaleFieldsOrRepairMissingOldOnes()
    {
        var document = JsonNode.Parse(ProjectFile.Serialize(new ProfileProject()))!.AsObject();
        document["SchemaVersion"] = 3;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
        var settings = document["Settings"]!.AsObject();
        settings.Remove("CrossHorizontalScale"); settings.Remove("CrossVerticalScale");
        settings.Remove("HorizontalScale");
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
        settings["HorizontalScale"] = "1000";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void EffectiveSettingsCopyPreservesTitleAndSharedStylingWithoutAliasing()
    {
        var settings = new ProfileSettings
        {
            Title = "Канал 12", HorizontalScale = 1000, VerticalScale = 100,
            CrossHorizontalScale = 200, CrossVerticalScale = 50,
            Datum = 101.123456789, TextHeightMm = 3.1, TextWidthFactor = .73,
            MetresPerDrawingUnit = .001, ShowEmptyRows = true
        };
        var effective = ProfileScales.ForKind(settings, ProfileKind.CrossSection);
        Assert.Equal(200, effective.HorizontalScale);
        Assert.Equal(50, effective.VerticalScale);
        Assert.Equal(settings.Title, effective.Title);
        Assert.Equal(settings.Datum, effective.Datum);
        Assert.Equal(settings.TextHeightMm, effective.TextHeightMm);
        Assert.Equal(settings.TextWidthFactor, effective.TextWidthFactor);
        Assert.Equal(settings.MetresPerDrawingUnit, effective.MetresPerDrawingUnit);
        Assert.True(effective.ShowEmptyRows);
        effective.Title = "Другой";
        effective.HorizontalScale = 400;
        Assert.Equal("Канал 12", settings.Title);
        Assert.Equal(1000, settings.HorizontalScale);
        Assert.Equal(200, settings.CrossHorizontalScale);
    }

    [Fact]
    public void SharedDataTransactionsPreserveBothScalePairsAndProjectIdentity()
    {
        var source = new ProfileProject();
        ProfileScales.Set(source.Settings, ProfileKind.Longitudinal, 1250.123456789, 125.987654321);
        ProfileScales.Set(source.Settings, ProfileKind.CrossSection, 200.123456789, 50.987654321);
        var result = ProjectSynchronization.SetSharedValue(source, 650, SharedProfileField.Water, 12);
        Assert.Equal(source.ProjectId, result.ProjectId);
        Assert.Equal(source.Settings.HorizontalScale, result.Settings.HorizontalScale);
        Assert.Equal(source.Settings.VerticalScale, result.Settings.VerticalScale);
        Assert.Equal(source.Settings.CrossHorizontalScale, result.Settings.CrossHorizontalScale);
        Assert.Equal(source.Settings.CrossVerticalScale, result.Settings.CrossVerticalScale);
    }
}
