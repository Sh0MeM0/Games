using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class ProfileTypeSwitchTests
{
    private static ProfileProject Reconstruction()
    {
        var project = SampleProjects.Longitudinal();
        return ProjectFile.Clone(project);
    }

    [Fact]
    public void DescribeListsTabsInOrderWithTypesAndDataWithoutCopying()
    {
        var source = ProfileCollection.Add(Reconstruction(), "Новый канал");
        source.Template = ProfileTemplate.NewChannel;
        var tabs = ProfileCollection.Describe(source);
        Assert.Equal(new[] { "Профиль 1", "Новый канал" }, tabs.Select(tab => tab.Name));
        Assert.Equal(new[] { ProfileTemplate.Reconstruction, ProfileTemplate.NewChannel }, tabs.Select(tab => tab.Template));
        Assert.Equal(new[] { true, false }, tabs.Select(tab => tab.HasData));
        Assert.Equal(new[] { 0, 1 }, tabs.Select(tab => tab.Index));
        Assert.Equal(source.ProfileId, tabs[1].ProfileId);
    }

    [Fact]
    public void FreshTabWithDefaultBlankSectionHasNoData()
    {
        var blank = new List<CrossSection>
        {
            new CrossSection
            {
                Name = "Поперечник 1", UsePointDistances = true,
                Points = new List<SectionPoint>
                {
                    new SectionPoint { Role = SectionPointRole.LeftBank, DistanceFromPrevious = 0 },
                    new SectionPoint { Role = SectionPointRole.Bottom },
                    new SectionPoint { Role = SectionPointRole.RightBank }
                }
            }
        };
        Assert.False(ProfileCollection.HasData(new List<LongitudinalPoint>(), blank));
        blank[0].Points[1].Existing = 0;
        Assert.True(ProfileCollection.HasData(new List<LongitudinalPoint>(), blank));
        blank[0].Points[1].Existing = null;
        blank[0].Points[2].DistanceFromPrevious = 3;
        Assert.True(ProfileCollection.HasData(new List<LongitudinalPoint>(), blank));
        Assert.True(ProfileCollection.HasData(new List<LongitudinalPoint> { new LongitudinalPoint { Station = 0 } }, null));
    }

    [Fact]
    public void ChangingTheTypeKeepsEveryValueIncludingFieldsOfTheOldType()
    {
        var source = Reconstruction();
        var changed = ProfileCollection.ChangeTemplate(source, source.ProfileId, ProfileTemplate.NewChannel);
        Assert.Equal(ProfileTemplate.NewChannel, changed.Template);
        Assert.Equal(ProjectFile.Serialize(Strip(source)), ProjectFile.Serialize(Strip(changed)));
        Assert.NotEmpty(ProfileEngine.Build(changed).Entities);
        var back = ProfileCollection.ChangeTemplate(changed, changed.ProfileId, ProfileTemplate.Reconstruction);
        Assert.Equal(ProjectFile.Serialize(source), ProjectFile.Serialize(back));
        // The source object is not modified.
        Assert.Equal(ProfileTemplate.Reconstruction, source.Template);
    }

    [Fact]
    public void ChangingTheTypeOfAnInactiveTabLeavesTheActiveOneAlone()
    {
        var first = Reconstruction();
        var two = ProfileCollection.Add(first, "Второй");
        var changed = ProfileCollection.ChangeTemplate(two, first.ProfileId, ProfileTemplate.Nvk);
        Assert.Equal(two.ProfileId, changed.ProfileId);
        Assert.Equal(two.Template, changed.Template);
        Assert.Equal(ProfileTemplate.Nvk, ProfileCollection.Describe(changed)[0].Template);
        Assert.Equal(first.Longitudinal.Count, ProfileCollection.Select(changed, first.ProfileId).Longitudinal.Count);
    }

    [Fact]
    public void UnknownTypeOrProfileIsRejected()
    {
        var source = Reconstruction();
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.ChangeTemplate(source, source.ProfileId, (ProfileTemplate)42));
        Assert.Throws<ProfileValidationException>(() => ProfileCollection.ChangeTemplate(source, "0123456789abcdef0123456789abcdef", ProfileTemplate.Nvk));
    }

    private static ProfileProject Strip(ProfileProject project)
    {
        var copy = ProjectFile.Clone(project);
        copy.Template = ProfileTemplate.Reconstruction;
        return copy;
    }
}
