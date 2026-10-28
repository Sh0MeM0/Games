using System;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProfileCollectionTests
{
    [Fact]
    public void NewTabHasIndependentSurveyAndKeepsProjectIdentityAndTabOrder()
    {
        var first = Project();
        string original = ProjectFile.Serialize(first);
        string firstId = first.ProfileId, sectionId = first.Sections[0].SectionId;
        var second = ProfileCollection.Add(first, "Канал Б");
        string secondId = second.ProfileId;
        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.NotEqual(firstId, secondId);
        Assert.Empty(second.Longitudinal);
        Assert.Empty(second.Sections);
        Assert.Equal("Канал Б", second.Settings.Title);
        Assert.Null(second.Settings.Datum);
        Assert.Equal(first.Settings.HorizontalScale, second.Settings.HorizontalScale);
        Assert.Equal(first.Settings.CrossHorizontalScale, second.Settings.CrossHorizontalScale);
        second.Longitudinal.Add(new LongitudinalPoint { Station = 30, ExistingBottom = 180 });
        second.Sections.Add(new CrossSection { Station = 30, ExistingBottom = 180 });
        second.Settings.HorizontalScale = 500;
        var third = ProfileCollection.Add(second, "Пруд");
        string thirdId = third.ProfileId;
        var activated = ProfileCollection.Select(third, firstId);
        Assert.Equal(new[] { firstId, secondId, thirdId }, ProfileCollection.Enumerate(activated).Select(profile => profile.ProfileId));
        Assert.Equal(0, activated.ActiveProfileIndex);
        Assert.Equal(sectionId, Assert.Single(activated.Sections).SectionId);
        Assert.Equal(140, Assert.Single(activated.Longitudinal).ExistingBottom);
        Assert.Equal(1000, activated.Settings.HorizontalScale);
        activated = ProfileCollection.Select(activated, secondId);
        Assert.Equal(1, activated.ActiveProfileIndex);
        Assert.Equal(180, Assert.Single(activated.Longitudinal).ExistingBottom);
        Assert.Equal(500, activated.Settings.HorizontalScale);
        Assert.Equal(new[] { firstId, secondId, thirdId }, ProfileCollection.Enumerate(activated).Select(profile => profile.ProfileId));
        Assert.Equal(original, ProjectFile.Serialize(first));
    }

    [Fact]
    public void JsonRoundTripPreservesInactiveProfilesAndSelectedTab()
    {
        var first = Project();
        var second = ProfileCollection.Add(first, "Второй канал");
        second.Template = ProfileTemplate.Nvk;
        second.Settings.CrossHorizontalScale = 200;
        second.Longitudinal.Add(new LongitudinalPoint { Station = 0, PipeInvert = 17.123456789 });
        var firstAgain = ProfileCollection.Select(second, first.ProfileId);
        var restored = ProjectFile.Deserialize(ProjectFile.Serialize(firstAgain));
        Assert.Equal(first.ProfileId, restored.ProfileId);
        Assert.Equal(0, restored.ActiveProfileIndex);
        var switched = ProfileCollection.Select(restored, second.ProfileId);
        Assert.Equal(ProfileTemplate.Nvk, switched.Template);
        Assert.Equal(200, switched.Settings.CrossHorizontalScale);
        Assert.Equal(17.123456789, Assert.Single(switched.Longitudinal).PipeInvert);
        Assert.Equal(first.Sections[0].SectionId, Assert.Single(switched.AdditionalProfiles).Sections[0].SectionId);
    }

    [Fact]
    public void RenameInactiveProfileKeepsSelectionGeometryAndCustomTitle()
    {
        var source = Project();
        source.Settings.Title = "Заказное название чертежа";
        var second = ProfileCollection.Add(source, "Канал Б");
        string before = ProjectFile.Serialize(second);
        var renamed = ProfileCollection.Rename(second, source.ProfileId, "  Канал А  ");
        Assert.Equal(second.ProfileId, renamed.ProfileId);
        Assert.Equal(second.ActiveProfileIndex, renamed.ActiveProfileIndex);
        var old = ProfileCollection.Select(renamed, source.ProfileId);
        Assert.Equal("Канал А", old.ProfileName);
        Assert.Equal("Заказное название чертежа", old.Settings.Title);
        Assert.Equal(source.Sections[0].SectionId, old.Sections[0].SectionId);
        Assert.Equal(before, ProjectFile.Serialize(second));
        var named = ProfileCollection.Rename(renamed, second.ProfileId, "Канал В");
        Assert.Equal("Канал В", named.Settings.Title);
    }

    [Fact]
    public void EnumeratedSnapshotsCannotModifySource()
    {
        var source = ProfileCollection.Add(Project(), "Канал Б");
        var snapshots = ProfileCollection.Enumerate(source);
        snapshots[0].Longitudinal[0].ExistingBottom = -100;
        snapshots[0].Sections[0].Name = "Изменённый";
        snapshots[1].Settings.Title = "Изменённый";
        Assert.Equal(140, source.AdditionalProfiles[0].Longitudinal[0].ExistingBottom);
        Assert.Equal("Сечение", source.AdditionalProfiles[0].Sections[0].Name);
        Assert.Equal("Канал Б", source.Settings.Title);
    }

    [Fact]
    public void DuplicateNamesAndUnknownTabsNeverChangeProject()
    {
        var source = ProfileCollection.Add(Project(), "Канал Б");
        string before = ProjectFile.Serialize(source);
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Add(source, "канал б"));
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Rename(source, source.ProfileId, "Профиль 1"));
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Rename(source, source.ProfileId, "\n"));
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Select(source, Guid.NewGuid().ToString("N")));
        Assert.Equal(before, ProjectFile.Serialize(source));
    }

    [Fact]
    public void ManualCrossSectionCreatesItsStationOnlyInTheActiveLongitudinalTab()
    {
        var first = Project();
        var second = ProfileCollection.Add(first, "Канал Б");
        var draft = new CrossSection { Name = "Ручной" };
        second.Sections.Add(draft);
        var edited = ProfileInterpolation.CloneSection(draft);
        edited.Station = 650;
        edited.LeftBank = 163.2;
        edited.RightBank = 163.5;
        edited.ExistingBottom = 161.75;
        edited.ProjectBottom = 161.1;
        edited.Water = 162.2;
        edited.BottomWidth = 1.5;
        edited.LeftSlope = 2;
        edited.RightSlope = 1.5;
        var changed = ProjectSynchronization.CommitSection(second, 0, draft, edited);
        var point = Assert.Single(changed.Longitudinal);
        Assert.Equal(650, point.Station);
        Assert.Equal(163.2, point.LeftBank);
        Assert.Equal(163.5, point.RightBank);
        Assert.Equal(161.75, point.ExistingBottom);
        Assert.Equal(161.1, point.ProjectBottom);
        Assert.Equal(162.2, point.Water);
        Assert.Equal(1.5, point.BottomWidth);
        Assert.Equal(2, point.LeftSlope);
        Assert.Equal(1.5, point.RightSlope);
        var untouched = ProfileCollection.Select(changed, first.ProfileId);
        Assert.Equal(0, Assert.Single(untouched.Longitudinal).Station);
        Assert.Equal(140, untouched.Longitudinal[0].ExistingBottom);
        Assert.Single(untouched.Sections);
    }

    [Fact]
    public void RemovingActiveMiddleTabSelectsNextAndPreservesOtherDataAndOrder()
    {
        var first = Project();
        var second = ProfileCollection.Add(first, "Второй");
        second.Longitudinal.Add(new() { Station = 5, ProjectBottom = 9 });
        second.Sections.Add(new() { Name = "Удаляемый поперечник" });
        var third = ProfileCollection.Add(second, "Третий");
        third.Settings.HorizontalScale = 500;
        var source = ProfileCollection.Select(third, second.ProfileId);
        string before = ProjectFile.Serialize(source);
        var result = ProfileCollection.Remove(source, second.ProfileId);
        Assert.Equal(third.ProfileId, result.ProfileId);
        Assert.Equal(1, result.ActiveProfileIndex);
        Assert.Equal(500, result.Settings.HorizontalScale);
        Assert.Empty(result.Longitudinal);
        Assert.Empty(result.Sections);
        Assert.Equal(new[] { first.ProfileId, third.ProfileId }, ProfileCollection.Enumerate(result).Select(p => p.ProfileId));
        var retained = ProfileCollection.Select(result, first.ProfileId);
        Assert.Equal(140, Assert.Single(retained.Longitudinal).ExistingBottom);
        Assert.Equal(first.Sections[0].SectionId, Assert.Single(retained.Sections).SectionId);
        Assert.Equal(before, ProjectFile.Serialize(source));
        Assert.Equal(result.ProfileId, ProjectFile.Clone(result).ProfileId);
    }

    [Fact]
    public void RemovingInactiveTabKeepsSelectionAndRemovingLastTabSelectsPrevious()
    {
        var first = Project();
        var second = ProfileCollection.Add(first, "Второй");
        var third = ProfileCollection.Add(second, "Третий");
        var result = ProfileCollection.Remove(third, first.ProfileId);
        Assert.Equal(third.ProfileId, result.ProfileId);
        Assert.Equal(1, result.ActiveProfileIndex);
        result = ProfileCollection.Remove(result, third.ProfileId);
        Assert.Equal(second.ProfileId, result.ProfileId);
        Assert.Equal(0, result.ActiveProfileIndex);
        Assert.Empty(result.AdditionalProfiles);
        string before = ProjectFile.Serialize(result);
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Remove(result, result.ProfileId));
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.Remove(result, "missing"));
        Assert.Equal(before, ProjectFile.Serialize(result));
    }

    private static ProfileProject Project() => new()
    {
        Name = "Общий проект",
        Settings = new ProfileSettings { Title = "ПРОДОЛЬНЫЙ ПРОФИЛЬ", Datum = 130, CrossHorizontalScale = 100, CrossVerticalScale = 100 },
        Longitudinal = new() { new() { Station = 0, ExistingBottom = 140 } },
        Sections = new() { new() { Name = "Сечение", Station = 0, ExistingBottom = 140 } }
    };
}
