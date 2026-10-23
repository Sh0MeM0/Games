using System;
using System.Linq;
using Profiles.Core;
using Xunit;
using static Profiles.Core.Tests.EngineGeometryTests;

namespace Profiles.Core.Tests;

public sealed class ProfileTemplateTests
{
    [Fact]
    public void ReconstructionUsesExplicitTemplateEvenWithoutExistingBottomOrBanks()
    {
        var project = Longitudinal(10, 12);
        project.Template = ProfileTemplate.Reconstruction;
        project.Settings.ShowEmptyRows = true;
        var labels = ProfileEngine.Build(project).Entities.OfType<DrawText>().Select(text => text.Text).ToList();
        Assert.Contains("ГЛУБИНА ПОДЧИСТКИ, М", labels);
        Assert.Contains("ОТМЕТКА ПРОЕКТНОЙ ПОДЧИСТКИ, М", labels);
        Assert.Contains("ОТМЕТКА СУЩЕСТВУЮЩЕГО ДНА, М", labels);
        Assert.DoesNotContain("ГЛУБИНА ТОРФА, М", labels);
    }

    [Fact]
    public void NewChannelHasPeatAndDesignBottomWithoutRenderingHiddenReconstructionData()
    {
        var project = Longitudinal(10, 12);
        project.Template = ProfileTemplate.NewChannel;
        project.Settings.Datum = null;
        foreach (var point in project.Longitudinal)
        {
            point.ProjectBottom = 8;
            point.PeatDepth = 1.25;
            point.ExistingBottom = -1000;
            point.RightBank = 1000;
            point.Water = -500;
        }
        var plan = ProfileEngine.Build(project);
        Assert.Equal(7, plan.Datum);
        var labels = plan.Entities.OfType<DrawText>().Select(text => text.Text).ToList();
        Assert.Contains("ГЛУБИНА ТОРФА, М", labels);
        Assert.Contains("ОТМЕТКА ПРОЕКТНОГО ДНА, М", labels);
        Assert.Contains("1.25", labels);
        Assert.DoesNotContain("ГЛУБИНА ПОДЧИСТКИ, М", labels);
        Assert.DoesNotContain("ОТМЕТКА СУЩЕСТВУЮЩЕГО ДНА, М", labels);
        foreach (var layer in new[] { "PROF_EXISTING", "PROF_RIGHT_BANK", "PROF_WATER" })
            Assert.Empty(Curves(plan, layer));
        Assert.Equal(-1000, project.Longitudinal[0].ExistingBottom);
    }

    [Fact]
    public void NvkRetainsEightStructuralRowsAndAddsMandatoryVolumeRowsWithoutChannelGround()
    {
        var project = Longitudinal(140, 142);
        project.Template = ProfileTemplate.Nvk;
        project.Settings.Datum = null;
        foreach (var point in project.Longitudinal) point.ProjectBottom = -100;
        var plan = ProfileEngine.Build(project);
        Assert.Equal(139, plan.Datum);
        var boundaries = plan.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_GRID" &&
            line.Points.Count == 2 && line.Points[0].X == -60 && line.Points[1].X == 20 &&
            line.Points[0].Y == line.Points[1].Y).ToList();
        Assert.Equal(12, boundaries.Count);
        var texts = plan.Entities.OfType<DrawText>().Select(text => text.Text).ToList();
        Assert.Contains("ОТМЕТКА НИЗА ИЛИ\nЛОТКА ТРУБЫ", texts);
        Assert.Contains("ПРОЕКТНАЯ ОТМЕТКА\nЗЕМЛИ", texts);
        Assert.Contains("ОБОЗНАЧЕНИЕ ТРУБЫ\nИ ТИП ИЗОЛЯЦИИ", texts);
        Assert.Contains("ОСНОВАНИЕ", texts);
        Assert.Contains("НОМЕР КОЛОДЦА, ТОЧКИ,\nУГЛА ПОВОРОТА", texts);
        Assert.Contains("1", texts);
        Assert.Contains("2", texts);
        Assert.DoesNotContain("П Л А Н", texts);
        Assert.Empty(Curves(plan, "PROF_PROJECT"));
        Assert.Empty(Curves(plan, "PROF_PROJECT_GROUND"));
        Assert.Empty(Curves(plan, "PROF_SLOPE"));
        var actual = Assert.Single(Curves(plan, "PROF_GROUND"));
        Assert.Equal(7, actual.ColorIndex);
        Assert.True(actual.Dashed);
    }

    [Fact]
    public void NvkUsesSeparateInterpolatedDesignGroundInvertAndUserPointNames()
    {
        var project = Longitudinal(140, 141, 142);
        project.Template = ProfileTemplate.Nvk;
        project.Longitudinal[1].Station = 110;
        project.Longitudinal[2].Station = 200;
        project.Longitudinal[0].ProjectGround = 141;
        project.Longitudinal[2].ProjectGround = 143;
        project.Longitudinal[0].PipeInvert = 138;
        project.Longitudinal[2].PipeInvert = 139;
        project.Longitudinal[0].PointName = "К-7";
        project.Longitudinal[0].PipeDescription = "ПЭ Ø160";
        project.Longitudinal[0].Foundation = "Песок";
        var plan = ProfileEngine.Build(project);
        var design = Assert.Single(Curves(plan, "PROF_PROJECT_GROUND"));
        Assert.Equal(1, design.ColorIndex);
        Assert.Equal(.4, design.LineWeightMm, 8);
        Assert.False(design.Dashed);
        Assert.Equal(1412, design.Points[1].Y, 8);
        Assert.Null(project.Longitudinal[1].ProjectGround);
        Assert.Null(project.Longitudinal[1].PipeInvert);
        Assert.Single(Curves(plan, "PROF_SLOPE"));
        var texts = plan.Entities.OfType<DrawText>().ToList();
        Assert.Contains(texts, text => text.Text == "1.00"); // 1 m / 100 m = 1 percent
        Assert.Contains(texts, text => text.Text == "138.10" && text.ColorIndex == 1);
        foreach (var label in new[] { "К-7", "ПЭ Ø160", "Песок" })
            Assert.Contains(texts, text => text.Text == label);
    }

    [Theory]
    [InlineData(144.30, true)]
    [InlineData(144.12, false)]
    public void SlopeRowSpansManualAnchorsAcrossInterpolatedStation(double end, bool rising)
    {
        var project = Longitudinal(146, 146, 146);
        project.Longitudinal[0].Station = 0;
        project.Longitudinal[1].Station = 63.49;
        project.Longitudinal[2].Station = 82.94;
        project.Longitudinal[0].ProjectBottom = 144.21;
        project.Longitudinal[2].ProjectBottom = end;
        var plan = ProfileEngine.Build(project);
        var slope = Assert.Single(Curves(plan, "PROF_SLOPE"));
        Assert.Equal(7, slope.ColorIndex);
        Assert.Equal(.3, slope.LineWeightMm, 8);
        Assert.Equal(0, slope.Points[0].X, 8);
        Assert.Equal(82.94, slope.Points[1].X, 8);
        Assert.Equal(rising, slope.Points[1].Y > slope.Points[0].Y);
        var texts = plan.Entities.OfType<DrawText>().ToList();
        Assert.Contains(texts, text => text.Text == "1.1");
        Assert.Contains(texts, text => text.Text == "82.94" && text.RotationDegrees == 0);
        double top = slope.Points.Max(point => point.Y), bottom = slope.Points.Min(point => point.Y);
        Assert.DoesNotContain(plan.Entities.OfType<DrawPolyline>(), line => line.Points.Count == 2 &&
            line.Points.All(point => Math.Abs(point.X - 63.49) < 1e-8) &&
            Math.Abs(line.Points.Max(point => point.Y) - top) < 1e-8 &&
            Math.Abs(line.Points.Min(point => point.Y) - bottom) < 1e-8);
        Assert.Null(project.Longitudinal[1].ProjectBottom);
        project.Longitudinal[1].ProjectBottom = 144.25;
        Assert.Equal(2, Curves(ProfileEngine.Build(project), "PROF_SLOPE").Count);
    }

    [Fact]
    public void LongUniformNetworkDescriptionExpandsItsRowInsteadOfOverlappingOtherRows()
    {
        var project = Longitudinal(140, 141);
        project.Template = ProfileTemplate.Nvk;
        foreach (var point in project.Longitudinal)
            point.PipeDescription = "Труба полиэтиленовая напорная с защитной оболочкой и дополнительной изоляцией";
        var plan = ProfileEngine.Build(project);
        var label = Assert.Single(plan.Entities.OfType<DrawText>(), text => text.Text == project.Longitudinal[0].PipeDescription);
        var boundaries = plan.Entities.OfType<DrawPolyline>().Where(line => line.Layer == "PROF_GRID" &&
            line.Points.Count == 2 && line.Points[0].X == -60 && line.Points[1].X == 20 &&
            line.Points[0].Y == line.Points[1].Y).Select(line => line.Points[0].Y).ToList();
        double top = boundaries.Where(y => y > label.Position.Y).Min();
        double bottom = boundaries.Where(y => y < label.Position.Y).Max();
        double estimatedHeight = (Math.Ceiling(label.Text.Length * label.Height * .75 * label.WidthFactor / label.MaxWidth) + 1) * label.Height * 1.3;
        Assert.True(top - bottom > estimatedHeight);
    }
}
