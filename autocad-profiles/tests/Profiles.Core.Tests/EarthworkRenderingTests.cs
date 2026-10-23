using System;
using System.Collections.Generic;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class EarthworkRenderingTests
{
    [Fact]
    public void EmptyLongitudinalQuantitiesKeepMandatoryBlankVolumeRowsAndRequestedLegend()
    {
        var project = Longitudinal(10, 11);
        project.Settings.ShowEmptyRows = false;
        var plan = ProfileEngine.Build(project);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        foreach (var name in new[] { "ОБЪЁМ СРЕЗКИ, М³", "ОБЪЁМ ДОСЫПКИ, М³", "ОБЪЁМ ЗАИЛЕНИЯ, М³" })
        {
            var label = Assert.Single(texts, text => text.Text == name);
            Assert.DoesNotContain(texts, text => text.Position.X >= 0 && text.Position.X <= 20 && Math.Abs(text.Position.Y - label.Position.Y) < 1e-8);
        }
        Assert.Contains(texts, text => text.Text == "Срезка = — (неполные данные)");
        Assert.DoesNotContain(texts, text => text.Text.StartsWith("ПЛОЩАДЬ", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Text == "Условные обозначения");
        foreach (var label in new[] { "Проектная отм. дна", "Существующая отм. дна", "Левый берег", "Правый берег", "Уровень воды" })
            Assert.Contains(texts, text => text.Text == label);
        var legend = Curves(plan, "PROF_LEGEND");
        Assert.Equal(new short[] { 1, 7, 7, 6, 5 }, legend.Select(line => line.ColorIndex));
        Assert.Equal(new[] { false, false, false, true, true }, legend.Select(line => line.Dashed));
    }

    [Theory]
    [InlineData(1000, 100, 1)]
    [InlineData(500, 100, .001)]
    public void CrossHatchesRepresentSeparateCutFillAndSiltAreasAtRequestedScale(double horizontal, double vertical, double metres)
    {
        var project = CutFillProject();
        project.Settings.HorizontalScale = horizontal;
        project.Settings.VerticalScale = vertical;
        project.Settings.MetresPerDrawingUnit = metres;
        var plan = ProfileEngine.Build(project);
        var hatches = plan.Entities.OfType<DrawHatch>().ToList();
        double areaFactor = horizontal / (vertical * metres * metres);
        Assert.Equal(1.28, hatches.Where(hatch => hatch.Layer == "PROF_CUT_HATCH").Sum(hatch => Area(hatch.Boundary)) / areaFactor, 7);
        Assert.Equal(2.88, hatches.Where(hatch => hatch.Layer == "PROF_FILL_HATCH").Sum(hatch => Area(hatch.Boundary)) / areaFactor, 7);
        Assert.Equal(1.60, hatches.Where(hatch => hatch.Layer == "PROF_SILT_HATCH").Sum(hatch => Area(hatch.Boundary)) / areaFactor, 7);
        Assert.All(hatches, hatch =>
        {
            Assert.True(Area(hatch.Boundary) > 0);
            Assert.InRange(hatch.PatternSpacing, double.Epsilon, 1.2 * horizontal / (1000 * metres));
            Assert.Equal(hatch.Layer == "PROF_CUT_HATCH" ? 1 : hatch.Layer == "PROF_FILL_HATCH" ? 3 : 30, hatch.ColorIndex);
        });
        Assert.NotEmpty(Curves(plan, "PROF_CLEANED"));
        Assert.Empty(plan.Entities.OfType<DrawCircle>());
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F срезки = 1.28 м²");
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F досыпки = 2.88 м²");
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F заиления = 1.60 м²");
    }

    [Fact]
    public void LongitudinalVolumesUseSectionStationsAcrossIntermediateLongitudinalPoints()
    {
        var project = Longitudinal(10, 10, 10);
        project.Longitudinal[0].Station = 0;
        project.Longitudinal[1].Station = 25;
        project.Longitudinal[2].Station = 100;
        var left = CutFillProject().Sections[0];
        var right = CutFillProject().Sections[0];
        left.Station = 0;
        right.Station = 100;
        project.Sections = new List<CrossSection> { left, right };
        var plan = ProfileEngine.Build(project);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        foreach (var pair in new[] { (Name: "ОБЪЁМ СРЕЗКИ, М³", Value: "128.0"), (Name: "ОБЪЁМ ДОСЫПКИ, М³", Value: "288.0"), (Name: "ОБЪЁМ ЗАИЛЕНИЯ, М³", Value: "160.0") })
        {
            var label = Assert.Single(texts, text => text.Text == pair.Name);
            var value = Assert.Single(texts, text => text.Position.X >= 0 && text.Position.X <= 100 && Math.Abs(text.Position.Y - label.Position.Y) < 1e-8);
            Assert.Equal(pair.Value, value.Text);
            Assert.Equal(50, value.Position.X, 8);
        }
        Assert.DoesNotContain(texts, text => text.Text.StartsWith("ПЛОЩАДЬ", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Text == "Срезка = 128.0 м³" && text.Position.X > 100);
        Assert.Contains(texts, text => text.Text == "Досыпка = 288.0 м³" && text.Position.X > 100);
        Assert.Contains(texts, text => text.Text == "Удаление ила = 160.0 м³" && text.Position.X > 100);
    }

    [Fact]
    public void NoDesignKeepsUnknownCutAndFillAnnotationsInsteadOfFabricatedZeros()
    {
        var project = CutFillProject();
        project.Sections[0].DesignMode = SectionDesignMode.None;
        var plan = ProfileEngine.Build(project);
        Assert.DoesNotContain(plan.Entities.OfType<DrawHatch>(), hatch => hatch.Layer == "PROF_CUT_HATCH" || hatch.Layer == "PROF_FILL_HATCH");
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F срезки = — м²");
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F досыпки = — м²");
        Assert.Contains(plan.Entities.OfType<DrawText>(), text => text.Text == "F заиления = 1.60 м²");
        Assert.NotEmpty(plan.Warnings);
    }

    [Theory]
    [InlineData("ПК 0+25.00")]
    [InlineData("Мост")]
    public void CrossIdentifierAndScalesAreCompactAtTheLeftAboveDatumWithoutGenericHeading(string name)
    {
        var project = CutFillProject();
        project.Sections[0].Name = name;
        project.Sections[0].Station = 25;
        var plan = ProfileEngine.Build(project);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        var identifier = Assert.Single(texts, text => text.Text == "ПК0+25.00");
        var datum = Assert.Single(texts, text => text.Text.StartsWith("УСЛ. ОТМ."));
        Assert.Equal(datum.Position.X, identifier.Position.X);
        Assert.Equal(18, identifier.Position.Y - datum.Position.Y, 8);
        Assert.True(identifier.Position.X < 0);
        Assert.DoesNotContain(texts, text => text.Text.Contains(project.Settings.Title));
        Assert.Contains(texts, text => text.Text == "ГОР.1:1000");
        Assert.Contains(texts, text => text.Text == "ВЕРТ.1:100");
    }

    [Fact]
    public void LongitudinalVolumesBetweenEnteredStationsDoNotPretendToCoverUnsurveyedEnds()
    {
        var project = Longitudinal(10, 10);
        project.Longitudinal[0].Station = 0;
        project.Longitudinal[1].Station = 100;
        var left = CutFillProject().Sections[0];
        var right = CutFillProject().Sections[0];
        left.Station = 20;
        right.Station = 80;
        project.Sections = new List<CrossSection> { left, right };
        var texts = ProfileEngine.Build(project).Entities.OfType<DrawText>().ToList();
        Assert.DoesNotContain(texts, text => text.Text.StartsWith("ПЛОЩАДЬ", StringComparison.Ordinal));
        var volumeLabel = Assert.Single(texts, text => text.Text == "ОБЪЁМ СРЕЗКИ, М³");
        var volume = Assert.Single(texts, text => text.Position.X >= 0 && text.Position.X <= 100 && Math.Abs(text.Position.Y - volumeLabel.Position.Y) < 1e-8);
        Assert.Equal("76.8", volume.Text);
        Assert.Equal(50, volume.Position.X, 8);
        Assert.Contains(texts, text => text.Text == "Срезка = 76.8 м³");
        Assert.Contains(texts, text => text.Text == "По рассчитанным участкам");
    }

    private static ProfileProject CutFillProject()
    {
        var project = Cross(new() { Offset = -2, Existing = 10, Design = 8 },
            new() { Offset = 0, Existing = 10, Design = 10 }, new() { Offset = 2, Existing = 10, Design = 12 });
        project.Sections[0].DesignMode = SectionDesignMode.Points;
        project.Sections[0].SiltThickness = .4;
        project.Sections[0].SiltLeft = -2;
        project.Sections[0].SiltRight = 2;
        return project;
    }

    private static double Area(List<Point2> boundary)
    {
        double twice = 0;
        for (int index = 0; index < boundary.Count; index++)
        {
            var a = boundary[index];
            var b = boundary[(index + 1) % boundary.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(twice) / 2;
    }
}
