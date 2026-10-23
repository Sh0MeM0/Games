using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class DrawingSurveyCapacityTests
{
    [Fact]
    public void InactiveProfilesConsumeBothGlobalBudgets()
    {
        var project = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint> { new() { Station = 0 }, new() { Station = 10 } },
            Sections = new List<CrossSection> { new() { Points = new List<SectionPoint> { new(), new() } } },
            AdditionalProfiles = new List<ProfileData>
            {
                new() { Name = "Другой", Longitudinal = new List<LongitudinalPoint> { new() { Station = 0 } },
                    Sections = new List<CrossSection> { new() { Points = new List<SectionPoint> { new(), new(), new() } }, new() } }
            }
        };
        var budget = DrawingSurveyCapacity.FromProject(project);
        Assert.Equal(9992, budget.RemainingPoints);
        Assert.Equal(197, budget.RemainingSections);
        string before = ProjectFile.Serialize(project);
        Assert.True(budget.TryAddLongitudinalStation(10));
        Assert.Equal(9992, budget.RemainingPoints);
        Assert.True(budget.TryAddLongitudinalStation(20));
        Assert.Equal(9991, budget.RemainingPoints);
        Assert.True(budget.TryAddLongitudinalStation(20));
        Assert.Equal(9991, budget.RemainingPoints);
        Assert.Equal(before, ProjectFile.Serialize(project));
    }

    [Fact]
    public void FullProjectStillAllowsExactActiveStationOverlayButNotInactiveStationCollision()
    {
        var project = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint> { new() { Station = 7 } },
            AdditionalProfiles = new List<ProfileData>
            {
                new() { Name = "Полный", Longitudinal = Enumerable.Range(0, 9999).Select(index => new LongitudinalPoint { Station = index }).ToList() }
            }
        };
        var budget = DrawingSurveyCapacity.FromProject(project);
        Assert.Equal(0, budget.RemainingPoints);
        Assert.True(budget.TryAddLongitudinalStation(7));
        Assert.False(budget.TryAddLongitudinalStation(8));
        Assert.False(budget.TryAddLongitudinalStation(10000));
        Assert.Equal(0, budget.RemainingPoints);
    }

    [Fact]
    public void LastGlobalSlotIsReservedOnceAndInvalidStationsDoNotConsumeIt()
    {
        var project = new ProfileProject
        {
            AdditionalProfiles = new List<ProfileData>
            {
                new() { Name = "Полный", Longitudinal = Enumerable.Range(0, 9999).Select(index => new LongitudinalPoint { Station = index }).ToList() }
            }
        };
        var budget = DrawingSurveyCapacity.FromProject(project);
        Assert.Throws<ProfileValidationException>(() => budget.TryAddLongitudinalStation(double.NaN));
        Assert.Throws<ProfileValidationException>(() => budget.TryAddLongitudinalStation(double.PositiveInfinity));
        Assert.Equal(1, budget.RemainingPoints);
        Assert.True(budget.TryAddLongitudinalStation(0));
        Assert.False(budget.TryAddLongitudinalStation(1));
        Assert.Equal(0, budget.RemainingPoints);
    }
}
