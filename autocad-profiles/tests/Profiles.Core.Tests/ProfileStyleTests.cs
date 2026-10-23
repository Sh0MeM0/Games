using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class ProfileStyleTests
{
    [Fact]
    public void LongitudinalLinesMatchRequestedLegendAndElevationLabelsHaveIndependentColors()
    {
        var project = Longitudinal(151.23, 151.33);
        foreach (var point in project.Longitudinal)
        {
            point.LeftBank = 152.34;
            point.RightBank = 153.45;
            point.ExistingBottom = 140.56;
            point.ProjectBottom = 139.78;
            point.Water = 145.67;
        }
        var plan = ProfileEngine.Build(project);
        CheckLine(plan, "PROF_EXISTING", 7, false);
        CheckLine(plan, "PROF_LEFT_BANK", 7, false);
        CheckLine(plan, "PROF_RIGHT_BANK", 6, true);
        CheckLine(plan, "PROF_PROJECT", 1, false);
        CheckLine(plan, "PROF_WATER", 5, true);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        foreach (var value in new[] { "151.23", "151.33", "152.34", "153.45", "140.56" })
        {
            var matching = texts.Where(text => text.Text == value).ToList();
            Assert.NotEmpty(matching);
            Assert.All(matching, text => Assert.Equal(7, text.ColorIndex));
        }
        Assert.All(texts.Where(text => text.Text == "139.78"), text => Assert.Equal(1, text.ColorIndex));
        Assert.All(texts.Where(text => text.Text == "145.67"), text => Assert.Equal(5, text.ColorIndex));
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
    }

    [Fact]
    public void CrossSectionHasNoVertexCirclesAndUsesRedProjectAndDashedBlueWater()
    {
        var project = Cross(new() { Offset = -6, Existing = 16 }, new() { Offset = 0, Existing = 10 },
            new() { Offset = 6, Existing = 16 });
        var section = project.Sections[0];
        section.ProjectBottom = 9;
        section.BottomWidth = 2;
        section.LeftSlope = section.RightSlope = .5;
        section.Water = 13;
        var plan = ProfileEngine.Build(project);
        CheckLine(plan, "PROF_EXISTING", 7, false);
        CheckLine(plan, "PROF_PROJECT", 1, false);
        CheckLine(plan, "PROF_WATER", 5, true);
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
        Assert.Equal(2, plan.Entities.OfType<DrawText>().Count(text => text.Text == "1:0.5" && text.ColorIndex == 1));
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "УР.ВОД.13.00" && text.ColorIndex == 5);
    }

    [Fact]
    public void IsolatedElevationsRemainInTableWithoutInventingCurveOrPointMarkers()
    {
        var project = Longitudinal(null, 12.34, null);
        var plan = ProfileEngine.Build(project);
        Assert.Empty(Curves(plan, "PROF_GROUND"));
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "12.34");
    }

    private static void CheckLine(DrawingPlan plan, string layer, short color, bool dashed)
    {
        var line = Assert.Single(Curves(plan, layer));
        Assert.Equal(color, line.ColorIndex);
        Assert.Equal(dashed, line.Dashed);
    }
}
