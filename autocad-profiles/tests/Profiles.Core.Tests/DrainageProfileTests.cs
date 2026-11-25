using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Profiles.Core;
using Xunit;

namespace Profiles.Core.Tests;

/// <summary>
/// The drainage collector profile against the reference sheet 095.26-РМС, лист 28
/// (collector «К-р 2'»). The PDF was plotted 1:1 on paper, so its millimetres are
/// compared with the plan converted to paper millimetres. As on those sheets the
/// drawing unit follows the vertical scale: at 1:100 one unit is one metre of
/// elevation, ten paper millimetres. Origin: ordinate 0 on the conditional horizon.
/// </summary>
public sealed class DrainageProfileTests
{
    private const double Paper = 0.1; // drawing units per paper millimetre at the vertical scale 1:100

    /// <summary>Collector «К-р 2'» from sheet 28.</summary>
    internal static ProfileProject K2() => new ProfileProject
    {
        Kind = ProfileKind.Longitudinal, Template = ProfileTemplate.Drainage, Name = "Лист 28", ProfileName = "К-р 2'",
        Settings = new ProfileSettings
        {
            Title = "К-р 2'", Subtitle = "к-л К-1 пк1+25", OutletElevation = 143.60,
            HorizontalScale = 2000, VerticalScale = 100, Datum = 140, TextWidthFactor = 1
        },
        Longitudinal = new List<LongitudinalPoint>
        {
            new LongitudinalPoint { Station = 0, Ground = 145.40, PeatDepth = .7, ProjectBottom = 144.10, PipeDescription = "125" },
            new LongitudinalPoint { Station = 28, PointName = "2-1" },
            new LongitudinalPoint { Station = 45, PipeDescription = "110" },
            new LongitudinalPoint { Station = 124, Ground = 145.70, PeatDepth = .7 },
            new LongitudinalPoint { Station = 150, PipeDescription = "90" },
            new LongitudinalPoint { Station = 174, Ground = 145.45, PeatDepth = .7 },
            new LongitudinalPoint { Station = 200, Ground = 145.55, PeatDepth = .7, ProjectBottom = 144.50 }
        }
    };

    private static DrawingPlan Plan(ProfileProject project = null) => ProfileEngine.Build(project ?? K2());

    private static IEnumerable<DrawPolyline> Lines(DrawingPlan plan, string layer) => plan.Entities.OfType<DrawPolyline>().Where(line => line.Layer == layer);

    private static DrawText Label(DrawingPlan plan, string text) => plan.Entities.OfType<DrawText>().Single(label => label.Text == text);

    private static double Mm(double units) => units / Paper;

    /// <summary>The sheet was drawn by hand to about 0.1 mm; the program draws exact values.</summary>
    private static void Near(double expected, double actual, double tolerance = 0.06) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, "Ожидалось " + expected + " ± " + tolerance + " мм, получено " + actual + " мм.");

    [Fact]
    public void HeadingTableHasTheReferenceRowsColumnAndGap()
    {
        var plan = Plan();
        var grid = Lines(plan, "PROF_GRID").ToList();
        // Row boundaries, mm below the conditional horizon: 5, 5, 10, 10, 10, 10.
        var horizontal = grid.Where(line => line.Points.Count == 2 && line.Points[0].Y == line.Points[1].Y && Mm(line.Points[0].X) < -39)
            .Select(line => Math.Round(Mm(line.Points[0].Y), 2)).Distinct().OrderByDescending(y => y).ToList();
        Assert.Equal(new[] { 0.0, -5, -10, -20, -30, -40, -50 }, horizontal);
        // A 35 mm heading column ending 5 mm before ordinate 0.
        var verticals = grid.Where(line => line.Points[0].X == line.Points[1].X && line.Points[0].Y == 0 && Mm(line.Points[1].Y) == -50)
            .Select(line => Math.Round(Mm(line.Points[0].X), 2)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { -40.0, -5 }, verticals);
        foreach (string heading in new[] { "№ ОРДИНАТ", "РАССТОЯНИЯ, М", "ОТМЕТКИ ПОВЕРХНОСТИ, М", "УКЛОН", "ПРОЕКТНЫЕ ОТМЕТКИ, М", "ДИАМЕТР" })
        {
            var label = Label(plan, heading);
            Near(-38.65, Mm(label.Position.X), 0.001);
            Assert.Equal(TextAlignment.Left, label.Alignment);
            Near(2.0, Mm(label.Height), 0.001);
        }
        Assert.Equal(2, plan.Entities.OfType<DrawText>().Count(label => label.Text == "ДЛИНА"));
        // The reference measures cap centres: № ОРДИНАТ in its 5 mm row, ОТМЕТКИ ПОВЕРХНОСТИ 3.34 mm below its top.
        Near(-2.5, Mm(Label(plan, "№ ОРДИНАТ").Position.Y), 0.001);
        Near(-13.34, Mm(Label(plan, "ОТМЕТКИ ПОВЕРХНОСТИ, М").Position.Y), 0.001);
        Near(-35, Mm(Label(plan, "ПРОЕКТНЫЕ ОТМЕТКИ, М").Position.Y), 0.001);
    }

    [Fact]
    public void DrawingUnitsFollowTheVerticalScaleAsOnTheSheets()
    {
        // Rows of 0.5 / 0.5 / 1 / 1 / 1 / 1 units, a 3.5-unit heading column, 24 m along the drain = 1.2 units at 1:2000 / 1:100.
        var plan = Plan();
        var rows = Lines(plan, "PROF_GRID").Where(line => line.Points[0].Y == line.Points[1].Y && line.Points[0].X < -3.9)
            .Select(line => Math.Round(line.Points[0].Y, 6)).Distinct().OrderByDescending(y => y).ToArray();
        Assert.Equal(new[] { 0, -0.5, -1, -2, -3, -4, -5 }, rows);
        var ground = Assert.Single(Lines(plan, "PROF_GROUND"));
        Assert.Equal(124 * 100.0 / 2000, ground.Points[1].X, 9);
        Assert.Equal(145.70 - 140, ground.Points[1].Y, 9);
        Assert.Equal(0.2, Label(plan, "124.0").Height, 9);
        // A channel profile keeps the units of its horizontal scale.
        var channel = ProfileEngine.Build(SampleProjects.NewChannel());
        Assert.Contains(channel.Entities.OfType<DrawPolyline>(), line => line.Points.Any(point => Math.Abs(point.X - 100) < 1e-9));
    }

    [Fact]
    public void CornerAboveTheHeadingColumnIsTheChannelCorner()
    {
        var plan = Plan();
        var datum = Label(plan, "УСЛ. ОТМ. 140.00");
        Near(-38.65, Mm(datum.Position.X), 0.001);
        Near(1.17 + 1.0, Mm(datum.Position.Y), 0.001);
        double Height(string text) => Mm(Label(plan, text).Position.Y);
        // From the table up: datum, legend under its heading, scales, then the receiver and the collector name.
        Assert.True(Height("Граница торфа") < Height("Поверхность земли"));
        Assert.True(Height("Поверхность земли") < Height("Проектная отм. дрены"));
        Assert.True(Height("Проектная отм. дрены") < Height("Условные обозначения"));
        Assert.True(Height("Условные обозначения") < Height("ВЕРТ. 1:100"));
        Assert.True(Height("ВЕРТ. 1:100") < Height("ГОР. 1:2000"));
        Assert.True(Height("ГОР. 1:2000") < Height("к-л К-1 пк1+25"));
        Assert.True(Height("к-л К-1 пк1+25") < Height("К-р 2'"));
        Near(5.0, Height("К-р 2'") - Height("к-л К-1 пк1+25"), 0.001);
        Near(3.0, Mm(Label(plan, "К-р 2'").Height), 0.001);
        // Everything of the corner stays left of ordinate 0, inside the heading column width.
        foreach (string text in new[] { "УСЛ. ОТМ. 140.00", "Условные обозначения", "ВЕРТ. 1:100", "ГОР. 1:2000", "К-р 2'", "к-л К-1 пк1+25" })
            Assert.InRange(Mm(Label(plan, text).Position.X), -38.66, -38.64);
        var legend = Lines(plan, "PROF_LEGEND").ToList();
        Assert.Equal(3, legend.Count);
        Assert.Contains(legend, line => line.ColorIndex == 1 && line.LineWeightMm == 0.40);
        Assert.Contains(legend, line => line.ColorIndex == 4 && line.Dashed && line.DashMm == 2.5);
        // The old sheet-only labels are gone: no «УСЛ. ГОР.», no level mark on the horizon.
        Assert.DoesNotContain(plan.Entities.OfType<DrawText>(), label => label.Text == "УСЛ. ГОР." || label.Text == "140.00");
        // The corner never meets the outlet mark: its texts are below or above the mark's band.
        double outletShelf = Mm(Lines(plan, "PROF_MARKS").Single(line => Math.Abs(Mm(line.Points[1].X) + 17.51) < .01).Points[1].Y);
        foreach (var label in plan.Entities.OfType<DrawText>().Where(label => Mm(label.Position.X) < -30 && Mm(label.Position.Y) > 0))
            Assert.False(Mm(label.Position.Y) > outletShelf - 3.7 - 2.2 && Mm(label.Position.Y) < outletShelf + 4.3,
                "«" + label.Text + "» пересекает знак отметки устья.");
    }

    [Fact]
    public void LinesAreQuarterMillimetreExceptTheRedDrainLine()
    {
        var plan = Plan();
        var lines = plan.Entities.OfType<DrawPolyline>().ToList();
        // The legend shows the drain line with its own weight.
        Assert.All(lines.Where(line => line.Layer != "PROF_DRAIN" && !(line.Layer == "PROF_LEGEND" && line.ColorIndex == 1)),
            line => Assert.Equal(0.25, line.LineWeightMm));
        var drain = Assert.Single(Lines(plan, "PROF_DRAIN"));
        Assert.Equal(0.40, drain.LineWeightMm);
        Assert.Equal(1, drain.ColorIndex);
        Assert.All(plan.Entities.OfType<DrawText>(), label => Assert.Equal(7, label.ColorIndex));
        // ISOCPEUR at width factor 1.0, as measured from the glyph advances in the PDF.
        Assert.All(plan.Entities.OfType<DrawText>(), label => Assert.Equal("isocpeur.ttf", label.FontFile));
        Assert.All(plan.Entities.OfType<DrawText>(), label => Assert.Equal(1.0, label.WidthFactor));
        Assert.All(Lines(plan, "PROF_PEAT"), line => Assert.Equal(4, line.ColorIndex));
        var peatBase = Assert.Single(Lines(plan, "PROF_PEAT"), line => line.Dashed);
        Assert.Equal(2.5, peatBase.DashMm);
        Assert.Equal(1.0, peatBase.GapMm);
    }

    [Fact]
    public void ProfileCurvesMatchTheReferenceSheet()
    {
        var plan = Plan();
        var ground = Assert.Single(Lines(plan, "PROF_GROUND"));
        // Sheet: (59.99, 48.08) (121.96, 45.07) (146.98, 47.57) (159.98, 46.59) relative to (59.99, 102.09).
        var expected = new[] { (0.0, 54.01), (61.97, 57.02), (86.99, 54.52), (99.99, 55.50) };
        Assert.Equal(expected.Length, ground.Points.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Near(expected[index].Item1, Mm(ground.Points[index].X));
            Near(expected[index].Item2, Mm(ground.Points[index].Y));
        }
        var drain = Assert.Single(Lines(plan, "PROF_DRAIN"));
        Near(41.02, Mm(drain.Points.First().Y));
        Near(45.04, Mm(drain.Points.Last().Y));
        var peatBase = Assert.Single(Lines(plan, "PROF_PEAT"), line => line.Dashed);
        Near(46.99, Mm(peatBase.Points.First().Y));
        Near(48.51, Mm(peatBase.Points.Last().Y));
    }

    [Fact]
    public void TableValuesFollowTheReference()
    {
        var plan = Plan();
        var texts = plan.Entities.OfType<DrawText>().Select(label => label.Text).ToList();
        foreach (string value in new[] { "0", "1", "2", "3", "124.0", "50.0", "26.0", "145.40", "145.70", "145.45", "145.55",
            "0.002", "200.0", "144.10", "144.50", "125", "45.0", "110", "105.0", "90", "К-р 2'", "к-л К-1 пк1+25", "УСЛ. ОТМ. 140.00", "143.60", "2-1" })
            Assert.Contains(value, texts);
        // Ordinate numbers only for survey rows: the inflow and pipe rows are not ordinates.
        Assert.DoesNotContain("4", texts);
        // Sheet ordinates at 59.99, 121.96, 146.98 and 159.98 mm: 0, 61.97, 86.99, 99.99 from ordinate 0.
        var ordinals = new[] { 0.0, 61.97, 86.99, 99.99 };
        for (int ordinal = 0; ordinal < ordinals.Length; ordinal++)
        {
            var number = Label(plan, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Near(ordinals[ordinal], Mm(number.Position.X));
            Near(-3.17, Mm(number.Position.Y));
        }
        // Cell texts: value 1.05 mm from the left border and 1.97 mm below the top, length 1.86 mm from the right border.
        var slope = Label(plan, "0.002");
        Near(1.05, Mm(slope.Position.X));
        Near(-21.97, Mm(slope.Position.Y));
        var length = Label(plan, "200.0");
        Assert.Equal(TextAlignment.Right, length.Alignment);
        Near(98.14, Mm(length.Position.X));
        Near(-27.74, Mm(length.Position.Y));
        var diameter = Label(plan, "125");
        Near(-41.97, Mm(diameter.Position.Y));
        var surface = Label(plan, "145.40");
        Assert.Equal(90, surface.RotationDegrees);
        Near(-15, Mm(surface.Position.Y));
    }

    [Fact]
    public void MarksTitleAndInflowFollowTheReference()
    {
        var plan = Plan();
        var marks = Lines(plan, "PROF_MARKS").ToList();
        // The inflow of «2-1» at 28 m: 14 mm right of ordinate 0, from the table to above the profile.
        var inflow = Assert.Single(marks, line => line.Points[0].X == line.Points[1].X && Mm(line.Points[0].X) > 13 && Mm(line.Points[0].X) < 15);
        Assert.Equal(0, inflow.Points[0].Y);
        Near(68.11, Mm(inflow.Points[1].Y), 0.15);
        var inflowLabel = Label(plan, "2-1");
        Assert.Equal(90, inflowLabel.RotationDegrees);
        Assert.Equal(TextAlignment.Right, inflowLabel.Alignment);
        Near(14 - 1.66, Mm(inflowLabel.Position.X));
        // Outlet mark: tip 5.04 mm before ordinate 0 at 143.60, stem 3.7 mm, shelf 12.47 mm to the left.
        Assert.Contains(marks, line => Math.Abs(Mm(line.Points[0].X) + 5.04) < .01 && Math.Abs(Mm(line.Points[0].Y) - 36) < .01 &&
            Math.Abs(Mm(line.Points[1].X) + 5.04) < .01 && Math.Abs(Mm(line.Points[1].Y) - 39.7) < .01);
        Assert.Contains(marks, line => Math.Abs(Mm(line.Points[1].X) + 17.51) < .01 && Math.Abs(Mm(line.Points[1].Y) - 39.7) < .01);
        var outlet = Label(plan, "143.60");
        Near(2.5, Mm(outlet.Height), 0.001);
        Assert.Equal(TextAlignment.Right, outlet.Alignment);
        Near(-7.08, Mm(outlet.Position.X));
    }

    [Fact]
    public void SlopeAndPipeCellsFollowGradeBreaksNotOrdinates()
    {
        var project = K2();
        project.Longitudinal[1].ProjectBottom = 144.20; // the inflow of «2-1» becomes a grade break
        var plan = Plan(project);
        var texts = plan.Entities.OfType<DrawText>().Select(label => label.Text).ToList();
        Assert.Contains("0.0036", texts);      // (144.20 − 144.10) / 28
        Assert.Contains("28.0", texts);
        Assert.Contains("0.0017", texts);      // (144.50 − 144.20) / 172
        Assert.Contains("172.0", texts);
        Assert.Contains("144.20", texts);
        // Repeating a diameter continues the same pipe.
        project = K2();
        project.Longitudinal[2].PipeDescription = "125";
        texts = Plan(project).Entities.OfType<DrawText>().Select(label => label.Text).ToList();
        Assert.Contains("150.0", texts);
        Assert.DoesNotContain("110", texts);
    }

    [Fact]
    public void ExtrapolatedEndOfTheDrainIsLabelledAndJoinsTheSlopeRow()
    {
        var project = K2();
        project.Longitudinal[6].ProjectBottom = null;
        project.Longitudinal[3].ProjectBottom = 144.348; // 124 m
        var plan = Plan(project);
        var texts = plan.Entities.OfType<DrawText>().Select(label => label.Text).ToList();
        Assert.Contains("144.35", texts);
        Assert.Contains("144.50", texts);      // 144.348 + 0.002 × 76 m, the same slope to the end
        Assert.Contains("76.0", texts);
        Assert.Equal(2, texts.Count(text => text == "0.002"));
    }

    [Fact]
    public void PeatMarksAreVShapesInsideTheLayer()
    {
        var plan = Plan();
        var strokes = Lines(plan, "PROF_PEAT").Where(line => !line.Dashed).ToList();
        Assert.NotEmpty(strokes);
        var full = strokes.Where(line => line.Points.Count == 3).ToList();
        Assert.NotEmpty(full);
        foreach (var mark in full)
        {
            // Two 2 mm strokes at 45° from a lower apex.
            Assert.Equal(Math.Sqrt(2), Mm(mark.Points[1].X - mark.Points[0].X), 6);
            Assert.Equal(Math.Sqrt(2), Mm(mark.Points[0].Y - mark.Points[1].Y), 6);
            Assert.Equal(Math.Sqrt(2), Mm(mark.Points[2].Y - mark.Points[1].Y), 6);
        }
        // Rows 2.83 mm apart.
        var rows = full.Select(mark => Math.Round(Mm(mark.Points[1].Y) / (2 * Math.Sqrt(2)), 6)).Distinct().ToList();
        Assert.All(rows, row => Assert.Equal(Math.Round(row), row, 6));
        // Nothing outside the 0.7 m layer: every stroke point between its base and the surface.
        foreach (var stroke in strokes)
            foreach (var point in stroke.Points)
            {
                Assert.InRange(Mm(point.X), -0.001, 100.001);
                Assert.InRange(Mm(point.Y), 46.9, 57.1);
            }
    }

    [Fact]
    public void PeatPatternClipsStrokesToThePolygon()
    {
        var square = new List<Point2> { new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10) };
        var inside = PeatPattern.Inside(new Point2(-5, 5), new Point2(15, 5), square);
        var part = Assert.Single(inside);
        Assert.Equal(0.25, part.Item1, 9);
        Assert.Equal(0.75, part.Item2, 9);
        Assert.Empty(PeatPattern.Inside(new Point2(-5, -5), new Point2(-1, -1), square));
        var marks = PeatPattern.Marks(square, 1, 1000);
        Assert.All(marks.SelectMany(mark => mark), point => { Assert.InRange(point.X, -1e-9, 10 + 1e-9); Assert.InRange(point.Y, -1e-9, 10 + 1e-9); });
        Assert.Null(PeatPattern.Marks(square, 0.01, 10));
    }

    [Fact]
    public void ProjectsWithoutDrainageStayVersionFiveFiles()
    {
        var channel = SampleProjects.NewChannel();
        string json = ProjectFile.Serialize(channel);
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(5, document["SchemaVersion"]!.GetValue<int>());
        Assert.False(document["Settings"]!.AsObject().ContainsKey("Subtitle"));
        Assert.False(document["Settings"]!.AsObject().ContainsKey("OutletElevation"));
        var restored = ProjectFile.Deserialize(json);
        Assert.Equal(6, restored.SchemaVersion);
        Assert.Equal("", restored.Settings.Subtitle);
        Assert.Null(restored.Settings.OutletElevation);
    }

    [Fact]
    public void DrainageProjectsRoundTripAsVersionSix()
    {
        var drainage = SampleProjects.Drainage();
        string json = ProjectFile.Serialize(drainage);
        var document = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(6, document["SchemaVersion"]!.GetValue<int>());
        Assert.Equal("Drainage", document["Template"]!.GetValue<string>());
        var restored = ProjectFile.Deserialize(json);
        Assert.Equal("к-л К-2 пк3+40", restored.Settings.Subtitle);
        Assert.Equal(144.58, restored.Settings.OutletElevation);
        Assert.Equal(json, ProjectFile.Serialize(restored));
        // A drainage tab in an inactive profile also needs version 6.
        var mixed = ProfileCollection.Add(SampleProjects.NewChannel(), "К-р 3'");
        mixed = ProfileCollection.ChangeTemplate(mixed, mixed.ProfileId, ProfileTemplate.Drainage);
        mixed = ProfileCollection.Select(mixed, mixed.AdditionalProfiles[0].ProfileId);
        Assert.Equal(ProfileTemplate.NewChannel, mixed.Template);
        Assert.Equal(6, JsonNode.Parse(ProjectFile.Serialize(mixed))!["SchemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void VersionFiveFilesCannotCarryVersionSixData()
    {
        var document = JsonNode.Parse(ProjectFile.Serialize(SampleProjects.NewChannel()))!.AsObject();
        document["Settings"]!["Subtitle"] = "к-л К-1";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
        document = JsonNode.Parse(ProjectFile.Serialize(SampleProjects.NewChannel()))!.AsObject();
        document["Template"] = "Drainage";
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
        document = JsonNode.Parse(ProjectFile.Serialize(SampleProjects.Drainage()))!.AsObject();
        document.Remove("SchemaVersion");
        document["SchemaVersion"] = 7;
        Assert.Throws<ProfileValidationException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }
}
