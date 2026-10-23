using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

public sealed class V07RenderingTests
{
    [Theory]
    [InlineData(ProfileTemplate.Reconstruction)]
    [InlineData(ProfileTemplate.NewChannel)]
    [InlineData(ProfileTemplate.Nvk)]
    public void PartialLongitudinalDataPreservesEveryTemplateRowAndLeavesUnknownValuesBlank(ProfileTemplate template)
    {
        var project = SampleProjects.ForTemplate(template);
        project.Settings.ShowEmptyRows = false;
        var full = ProfileEngine.Build(project);
        project.Settings.Datum = 0;
        project.Sections.Clear();
        project.Longitudinal = new List<LongitudinalPoint>
        {
            new() { Station = 0, Ground = 10 }, new() { Station = 100, Ground = 11 }
        };
        var partial = ProfileEngine.Build(project);
        if (template == ProfileTemplate.Reconstruction)
            Assert.DoesNotContain(partial.Entities.OfType<DrawText>(), text => text.Text == "ОТМЕТКИ ПОВЕРХНОСТИ ЗЕМЛИ, М");
        Assert.Equal(full.Entities.OfType<DrawText>().Where(text => text.Position.X < 0 && text.Position.Y < 0).Select(text => text.Text),
            partial.Entities.OfType<DrawText>().Where(text => text.Position.X < 0 && text.Position.Y < 0).Select(text => text.Text));
        Assert.DoesNotContain(partial.Entities.OfType<DrawText>(), text => text.Position.Y < 0 && (text.ColorIndex == 1 || text.ColorIndex == 5));
        Assert.DoesNotContain(partial.Entities.OfType<DrawPolyline>(), line => line.Layer == "PROF_PROJECT" || line.Layer == "PROF_PROJECT_GROUND"
            || line.Layer == "PROF_PIPE_INVERT" || line.Layer == "PROF_WATER" || line.Layer == "PROF_EXISTING");
        Assert.Equal(full.Entities.OfType<DrawPolyline>().Count(line => line.Layer == "PROF_LEGEND"),
            partial.Entities.OfType<DrawPolyline>().Count(line => line.Layer == "PROF_LEGEND"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CrossTableAlwaysContainsFourFixedRowsWhenOneProfileIsMissing(bool designOnly, bool legacyEmptyRows)
    {
        var project = new ProfileProject
        {
            Kind = ProfileKind.CrossSection,
            Settings = new ProfileSettings { Datum = 0, CrossHorizontalScale = 100, CrossVerticalScale = 100, ShowEmptyRows = legacyEmptyRows },
            Sections = new List<CrossSection> { new()
            {
                DesignMode = designOnly ? SectionDesignMode.Points : SectionDesignMode.None,
                Points = new List<SectionPoint> {
                    new() { Offset = 0, Existing = designOnly ? null : 10, Design = designOnly ? 10 : null },
                    new() { Offset = 10, Existing = designOnly ? null : 11, Design = designOnly ? 11 : null }
                }
            } }
        };
        var plan = ProfileEngine.Build(project);
        var rows = plan.Entities.OfType<DrawText>().Where(text => text.Position.X < 0 && text.Position.Y < 0).ToList();
        Assert.Equal(new[] { "№ ТОЧКИ", "РАССТОЯНИЕ, м", "ОТМЕТКИ\nПОВЕРХНОСТИ, м", "ПРОЕКТНЫЕ\nОТМЕТКИ, м" }, rows.Select(text => text.Text));
        var boundaries = plan.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_GRID" && line.Points.Count == 2
            && line.Points[0].X < 0 && line.Points[1].X > 0 && line.Points[0].Y == line.Points[1].Y).Select(line => line.Points[0].Y).ToList();
        Assert.Equal(5, boundaries.Count);
        var expected = new[] { 0.0, -.6, -1.1, -2.3, -3.5 };
        for (int index = 0; index < expected.Length; index++) Assert.Equal(expected[index], boundaries[index], 8);
        double top = designOnly ? -1.1 : -2.3, bottom = designOnly ? -2.3 : -3.5;
        Assert.DoesNotContain(plan.Entities.OfType<DrawText>(), text => text.Position.X >= 0 && text.Position.Y < top && text.Position.Y > bottom);
        Assert.DoesNotContain(plan.Entities.OfType<DrawPolyline>(), line => line.Layer == (designOnly ? "PROF_EXISTING" : "PROF_PROJECT"));
    }

    [Theory]
    [InlineData(ProfileTemplate.Reconstruction)]
    [InlineData(ProfileTemplate.NewChannel)]
    [InlineData(ProfileTemplate.Nvk)]
    public void EntireLongitudinalHeaderRemainsInLeftMarginAndDoesNotFollowHighSurveyPoints(ProfileTemplate template)
    {
        var project = SampleProjects.ForTemplate(template);
        project.Settings.Datum = 0;
        var before = ProfileEngine.Build(project);
        var previousHeader = before.Entities.OfType<DrawText>().Where(text => text.Position.X < 0 && text.Position.Y > 0).ToList();
        project.Longitudinal.Last().Ground = 1000;
        if (template == ProfileTemplate.Reconstruction) project.Longitudinal.Last().RightBank = 1001;
        var after = ProfileEngine.Build(project);
        var header = after.Entities.OfType<DrawText>().Where(text => text.Position.X < 0 && text.Position.Y > 0).ToList();

        Assert.Equal(previousHeader.Select(text => (text.Text, text.Position)), header.Select(text => (text.Text, text.Position)));
        Assert.All(header, text =>
        {
            Assert.True(text.MaxWidth > 0);
            Assert.True(text.Position.X + text.MaxWidth < 0);
        });
        var title = Assert.Single(header, text => text.Text == project.Settings.Title);
        double paper = project.Settings.HorizontalScale / (1000 * project.Settings.MetresPerDrawingUnit);
        Assert.InRange(title.Position.Y / paper, 10, 70);
        var legend = after.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_LEGEND").ToList();
        Assert.NotEmpty(legend);
        Assert.All(legend.SelectMany(line => line.Points), point => Assert.True(point.X < 0 && point.Y > 0));
        var datum = Assert.Single(header, text => text.Text.StartsWith("УСЛ. ОТМ.", StringComparison.Ordinal));
        Assert.True(legend.Min(line => line.Points[0].Y) > datum.Position.Y + datum.Height);
        Assert.Equal(before.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_LEGEND").SelectMany(line => line.Points),
            legend.SelectMany(line => line.Points));
    }
}
