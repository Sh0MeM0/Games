using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class PlaneTests
    {
        [Fact]
        public void ThreePointsGiveTheirPlane()
        {
            Func<XY, double> plane = p => 150.25 + 0.012 * (p.X - 5_500_000) - 0.004 * (p.Y - 4_300_000);
            var a = new XY(5_500_010, 4_300_005);
            var b = new XY(5_500_060, 4_300_020);
            var c = new XY(5_500_030, 4_300_090);
            var definition = PlaneDefinition.FromThreePoints(a, plane(a), b, plane(b), c, plane(c));
            foreach (var q in new[] { new XY(5_500_000, 4_300_000), new XY(5_500_123.4, 4_300_77.7) })
                Assert.Equal(plane(q), definition.ZAt(q), 9);
            Assert.Equal(Math.Sqrt(12 * 12 + 4 * 4), definition.MaxSlopePermille, 9);
        }

        [Fact]
        public void CollinearPointsAreRefused()
        {
            Assert.Throws<KzmException>(() => PlaneDefinition.FromThreePoints(new XY(0, 0), 1, new XY(10, 10), 2, new XY(20, 20), 3));
        }

        [Fact]
        public void SlopesAlongAndAcross()
        {
            // Direction 30°: +15 ‰ along, −5 ‰ across (to the left).
            var origin = new XY(100, 200);
            var plane = PlaneDefinition.FromSlopes(origin, 145.00, 30, 15, -5);
            double a = 30 * Math.PI / 180;
            var along = new XY(origin.X + 100 * Math.Cos(a), origin.Y + 100 * Math.Sin(a));
            var across = new XY(origin.X - 100 * Math.Sin(a), origin.Y + 100 * Math.Cos(a));
            Assert.Equal(145.00, plane.ZAt(origin), 9);
            Assert.Equal(146.50, plane.ZAt(along), 9);
            Assert.Equal(144.50, plane.ZAt(across), 9);
            Assert.Equal(15, plane.SlopeAlongPermille(30), 9);
            Assert.Equal(Math.Sqrt(15 * 15 + 5 * 5), plane.MaxSlopePermille, 9);
        }

        [Fact]
        public void PercentInsteadOfPermilleIsCaught()
        {
            Assert.Throws<KzmException>(() => PlaneDefinition.FromSlopes(new XY(0, 0), 100, 0, 1500, 0));
        }
    }

    public class ControlVolumeTests
    {
        private static Tin Flat(double z, double size = 60, double step = 10)
        {
            var points = new List<XY>();
            for (double x = 0; x <= size + 1e-9; x += step)
            for (double y = 0; y <= size + 1e-9; y += step)
                points.Add(new XY(1000 + x, 2000 + y));
            return Tin.Build(points, points.Select(_ => z).ToList());
        }

        [Fact]
        public void TwoPlanesGiveTheExactPrism()
        {
            var boundary = new List<XY> { new XY(1005, 2005), new XY(1045, 2005), new XY(1045, 2025), new XY(1005, 2025) };
            var result = ControlVolume.Compute(boundary, Flat(100.00), Flat(100.50));
            Assert.Equal(400, result.Fill, 6);
            Assert.Equal(0, result.Cut, 9);
            Assert.Equal(800, result.Area, 6);
            Assert.Equal(0, result.MissingArea, 9);
        }

        [Fact]
        public void AgreesWithFineSquaresOnRoughSurfaces()
        {
            var random = new Random(21);
            var fx = new List<XY>(); var fz = new List<double>();
            var px = new List<XY>(); var pz = new List<double>();
            for (int i = 0; i < 400; i++)
            {
                var p = new XY(5_500_000 + random.NextDouble() * 100, 4_300_000 + random.NextDouble() * 100);
                fx.Add(p); fz.Add(100 + 0.6 * Math.Sin(p.X / 9) + 0.4 * Math.Cos(p.Y / 7));
                var q = new XY(5_500_000 + random.NextDouble() * 100, 4_300_000 + random.NextDouble() * 100);
                px.Add(q); pz.Add(100.1 + 0.002 * (q.X - 5_500_000));
            }
            foreach (var corner in new[] { new XY(5_500_000, 4_300_000), new XY(5_500_100, 4_300_000), new XY(5_500_100, 4_300_100), new XY(5_500_000, 4_300_100) })
            {
                fx.Add(corner); fz.Add(100);
                px.Add(corner); pz.Add(100.1 + 0.002 * (corner.X - 5_500_000));
            }
            var fact = Tin.Build(fx, fz);
            var project = Tin.Build(px, pz);
            var boundary = new List<XY>
            {
                new XY(5_500_010, 4_300_010), new XY(5_500_090, 4_300_015), new XY(5_500_085, 4_300_060),
                new XY(5_500_050, 4_300_050), new XY(5_500_045, 4_300_090), new XY(5_500_012, 4_300_085),
            };
            var control = ControlVolume.Compute(boundary, fact, project);
            // Reference: midpoint sums of max(h, 0) and max(−h, 0) on a 0,25 m lattice.
            double fill = 0, cut = 0, cell = 0.25;
            for (double x = 5_500_000 + cell / 2; x < 5_500_100; x += cell)
            for (double y = 4_300_000 + cell / 2; y < 4_300_100; y += cell)
            {
                var p = new XY(x, y);
                if (!Geometry.Contains(boundary, p)) continue;
                double h = project.ZAt(p).Value - fact.ZAt(p).Value;
                if (h > 0) fill += h * cell * cell; else cut -= h * cell * cell;
            }
            Assert.InRange(Math.Abs(control.Fill - fill), 0, 0.005 * fill + 0.5);
            Assert.InRange(Math.Abs(control.Cut - cut), 0, 0.005 * cut + 0.5);
            Assert.Equal(Geometry.Area(boundary.Select(p => new XY(p.X - 5_500_000, p.Y - 4_300_000)).ToList()), control.Area, 3);

            // The method of squares on a fine grid tends to the same volumes.
            var grid = new GridDefinition(1, new XY(5_500_000, 4_300_000), 0);
            var squares = CartogramCalculator.Compute(boundary, fact.ZAt, project.ZAt, grid);
            Assert.InRange(Math.Abs(squares.TotalFill - control.Fill), 0, 0.01 * control.Fill + 1);
            Assert.InRange(Math.Abs(squares.TotalCut - control.Cut), 0, 0.01 * control.Cut + 1);
        }

        [Fact]
        public void SurfaceOutlineLimitsTheData()
        {
            var points = new List<XY>();
            for (int x = 0; x <= 60; x += 10)
            for (int y = 0; y <= 60; y += 10)
                points.Add(new XY(x, y));
            var fact = Tin.Build(points, points.Select(_ => 10.0).ToList(), null,
                new List<XY> { new XY(0, 0), new XY(30, 0), new XY(30, 60), new XY(0, 60) });
            var project = Tin.Build(points, points.Select(_ => 11.0).ToList());
            var boundary = new List<XY> { new XY(10, 10), new XY(50, 10), new XY(50, 50), new XY(10, 50) };
            var result = ControlVolume.Compute(boundary, fact, project);
            Assert.Equal(800, result.Area, 6);
            Assert.Equal(800, result.MissingArea, 6);
            Assert.Equal(800, result.Fill, 6);
        }

        [Fact]
        public void DifferenceInCubicAndPercent()
        {
            var (cubic, percent) = ControlVolumeResult.Difference(205, 200);
            Assert.Equal(5, cubic, 9);
            Assert.Equal(2.5, percent.Value, 9);
            Assert.Null(ControlVolumeResult.Difference(0, 0).Percent);
        }
    }

    public class ExportTests
    {
        private static CartogramReport Report()
        {
            var points = new List<XY>();
            for (int x = 0; x <= 60; x += 10)
            for (int y = 0; y <= 40; y += 10)
                points.Add(new XY(1000 + x, 2100 + y));
            var fact = Tin.Build(points, points.Select(_ => 100.0).ToList());
            var project = Tin.Build(points, points.Select(_ => 100.5).ToList());
            var boundary = new List<XY> { new XY(1005, 2105), new XY(1045, 2105), new XY(1045, 2125), new XY(1005, 2125) };
            var result = CartogramCalculator.Compute(boundary, fact.ZAt, project.ZAt, new GridDefinition(20, new XY(1000, 2100), 0));
            return new CartogramReport
            {
                Info = new CartogramInfo { Name = "КЗМ-1 <тест> & «кавычки»" },
                Result = result,
                FactName = "Факт",
                ProjectName = "Проект",
                Control = ControlVolume.Compute(boundary, fact, project),
                Created = "2026-11-12 10:00",
            };
        }

        [Fact]
        public void XlsxIsAValidPackage()
        {
            var sheets = CartogramExport.Sheets(Report());
            var stream = new MemoryStream();
            XlsxWriter.Write(stream, sheets);
            string copy = Environment.GetEnvironmentVariable("KZM_XLSX_OUT");
            if (!string.IsNullOrEmpty(copy)) File.WriteAllBytes(copy, stream.ToArray());
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Contains("[Content_Types].xml", names);
                Assert.Contains("_rels/.rels", names);
                Assert.Contains("xl/workbook.xml", names);
                Assert.Contains("xl/styles.xml", names);
                Assert.Contains("xl/worksheets/sheet3.xml", names);
                XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml").Open());
                Assert.Equal(new[] { "Узлы", "Квадраты", "Итоги" }, workbook.Descendants(main + "sheet").Select(e => (string)e.Attribute("name")).ToArray());
                foreach (var entry in zip.Entries) XDocument.Load(entry.Open()); // every part is well-formed XML
                var totals = XDocument.Load(zip.GetEntry("xl/worksheets/sheet3.xml").Open());
                var texts = totals.Descendants(main + "t").Select(t => t.Value).ToList();
                Assert.Contains("КЗМ-1 <тест> & «кавычки»", texts);
                Assert.Contains("По картограмме (метод квадратов)", texts);
                var row = totals.Descendants(main + "row").First(r => r.Descendants(main + "t").Any(t => t.Value == "По картограмме (метод квадратов)"));
                var values = row.Descendants(main + "v").Select(v => double.Parse(v.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Assert.Equal(400, values[0], 6);
                Assert.Equal(0, values[1], 9);
            }
        }

        [Fact]
        public void CsvUsesSemicolonsAndDecimalComma()
        {
            var stream = new MemoryStream();
            CsvWriter.Write(stream, CartogramExport.Sheets(Report()));
            var bytes = stream.ToArray();
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Contains("По картограмме (метод квадратов);400,00;0,00", text);
            Assert.Contains("Столбец;Ряд;Площадь, м²", text);
        }

        [Fact]
        public void ColumnNames()
        {
            Assert.Equal("A", XlsxWriter.Column(0));
            Assert.Equal("Z", XlsxWriter.Column(25));
            Assert.Equal("AA", XlsxWriter.Column(26));
            Assert.Equal("AZ", XlsxWriter.Column(51));
            Assert.Equal("BA", XlsxWriter.Column(52));
        }
    }

    public class Stage5RecordTests
    {
        [Fact]
        public void LabelTemplateRoundTrips()
        {
            var c = new CartogramInfo { Id = "x", Name = "К" };
            c.WorkingLabel.Corner = LabelCorner.BottomLeft;
            c.WorkingLabel.Color = "ACI:1";
            c.FactLabel.Layer = "Мой слой";
            c.ControlCheck = false;
            c.LastControlFill = 12.5;
            var map = CartogramInfo.Encode(c).ToDictionary(p => p.Key, p => p.Value);
            var back = CartogramInfo.Decode("x", map);
            Assert.Equal(LabelCorner.BottomLeft, back.WorkingLabel.Corner);
            Assert.Equal("ACI:1", back.WorkingLabel.Color);
            Assert.Equal("Мой слой", back.FactLabel.Layer);
            Assert.False(back.ControlCheck);
            Assert.Equal(12.5, back.LastControlFill);
            Assert.True(back.KeepMovedLabels);
        }

        [Fact]
        public void DefaultLabelsDoNotChangeTheFingerprintOfOldCartograms()
        {
            var c = new CartogramInfo { Id = "x", Name = "К", FactSurfaceId = "f", ProjectSurfaceId = "p", BoundaryHandle = "1A" };
            var keys = CartogramInfo.FingerprintPairs(c).Select(p => p.Key).ToList();
            Assert.Equal(new[] { "fact", "project", "boundary", "step", "ox", "oy", "angle", "columns", "rows", "scale", "node_mm", "volume_mm", "totals_mm", "volume_dec" }, keys);
            c.ProjectLabel.Corner = LabelCorner.BottomLeft;
            Assert.Contains("lbl_p", CartogramInfo.FingerprintPairs(c).Select(p => p.Key));
        }

        [Fact]
        public void LabelsInOneCornerAreReported()
        {
            var c = new CartogramInfo { Id = "x", Name = "К", FactSurfaceId = "f", ProjectSurfaceId = "p", BoundaryHandle = "1A" };
            Assert.Empty(c.Validate());
            c.FactLabel.Corner = LabelCorner.TopLeft;
            Assert.Contains(c.Validate(), p => p.Contains("одном углу"));
            c.FactLabel.Corner = LabelCorner.BottomRight;
            c.FactLabel.Layer = "плохой|слой";
            Assert.Contains(c.Validate(), p => p.Contains("слой подписи"));
        }

        [Fact]
        public void StructureLineRecordKeepsOrder()
        {
            var line = new StructureLine { Id = "l1", Name = "Бортовой камень", Closed = true, SourceHandle = "2F" };
            line.PointHandles.AddRange(new[] { "A1", "A2", "A3" });
            var back = StructureLine.Decode("l1", StructureLine.Encode(line));
            Assert.Equal(new[] { "A1", "A2", "A3" }, back.PointHandles);
            Assert.True(back.Closed);
            Assert.Equal("Бортовой камень", back.Name);
            Assert.Equal(new[] { ("A1", "A2"), ("A2", "A3"), ("A3", "A1") }, back.Segments(null).ToArray());
            Assert.Equal(new[] { ("A1", "A3") }, back.Segments(h => h != "A2").Take(1).ToArray());
            Assert.Equal(string.Empty, StructureLine.FingerprintOf(new StructureLine[0]));
        }

        [Fact]
        public void DefaultsTemplateRoundTrips()
        {
            var settings = new KzmSettings { Decimals = 3, Scale = 1000 };
            var template = new CartogramInfo { Step = 10, VolumeDecimals = 1, Rows = LabelScheme.Numbers };
            template.FactLabel.Corner = LabelCorner.BottomLeft;
            var (s, t) = DefaultsCodec.Decode(DefaultsCodec.Encode(settings, template));
            Assert.Equal(3, s.Decimals);
            Assert.Equal(1000, s.Scale);
            Assert.Equal(10, t.Step);
            Assert.Equal(1, t.VolumeDecimals);
            Assert.Equal(LabelScheme.Numbers, t.Rows);
            Assert.Equal(LabelCorner.BottomLeft, t.FactLabel.Corner);
            var fresh = new CartogramInfo();
            fresh.TakeTemplate(t);
            Assert.Equal(10, fresh.Step);
            Assert.Equal(LabelCorner.BottomLeft, fresh.FactLabel.Corner);
        }

        [Fact]
        public void SourceKindsRoundTrip()
        {
            foreach (SourceKind kind in Enum.GetValues(typeof(SourceKind)))
                Assert.Equal(kind, SourceKinds.Parse(SourceKinds.Code(kind)));
        }
    }
}

namespace Kzm.Core.Tests
{
    /// <summary>The check of VALIDATION.md, step «Плоскость»: the numbers given there.</summary>
    public class PlatformScenarioTests
    {
        [Fact]
        public void PlaneOnTheTestBoundaryGives600()
        {
            // KZM_TEST_CARTO: fact texts 100,00 every 10 m over 60 × 40 m at (1000; 2100); boundary 40 × 20 m.
            var fact = new List<XY>();
            for (int i = 0; i <= 6; i++)
            for (int j = 0; j <= 4; j++)
                fact.Add(new XY(1000 + 10 * i, 2100 + 10 * j));
            var factTin = Tin.Build(fact, fact.Select(_ => 100.00).ToList());
            // Plane: point (1000; 2100), Z 100,50, direction 0°, +10 ‰ along, 0 ‰ across; points in the boundary vertices,
            // the contour is a structure line.
            var plane = PlaneDefinition.FromSlopes(new XY(1000, 2100), 100.50, 0, 10, 0);
            var corners = new List<XY> { new XY(1005, 2105), new XY(1045, 2105), new XY(1045, 2125), new XY(1005, 2125) };
            Assert.Equal(100.55, plane.ZAt(corners[0]), 9);
            Assert.Equal(100.95, plane.ZAt(corners[1]), 9);
            var projectTin = Tin.Build(corners, corners.Select(plane.ZAt).ToList(), null, null, new[] { (0, 1), (1, 2), (2, 3), (3, 0) });
            var result = CartogramCalculator.Compute(corners, factTin.ZAt, projectTin.ZAt, new GridDefinition(20, new XY(1000, 2100), 0));
            Assert.Equal(600, result.TotalFill, 6);
            Assert.Equal(0, result.TotalCut, 9);
            Assert.True(result.Balanced);
            var control = ControlVolume.Compute(corners, factTin, projectTin);
            Assert.Equal(600, control.Fill, 6);
            Assert.Equal(0, control.MissingArea, 9);
        }
    }
}

namespace Kzm.Core.Tests
{
    /// <summary>KZM_TEST_SOURCES of examples/kzm-test-data.lsp as the import service hands it to the planner.</summary>
    public class SourcesTestDataTests
    {
        [Fact]
        public void PointsWithZ()
        {
            // Sources.ReadPoint writes Z with a decimal part; the POINT with Z = 0 is not a source (reported separately).
            var texts = new List<SourceText>
            {
                new SourceText("P1", SourceKind.Point, "150.25", "150.25", new XY(1010, 2200)),
                new SourceText("P2", SourceKind.Point, "150.0", "150.0", new XY(1020, 2200)),
            };
            var plan = ImportPlanner.Plan(texts, null, null, new ImportOptions(XyMode.Insertion, 2));
            PointNumbering.Assign(plan, 1);
            Assert.Equal(new[] { 150.25, 150.0 }, plan.Accepted.Select(i => i.Elevation.Value));
            Assert.Equal(new[] { "1", "2" }, plan.Accepted.Select(i => i.Number));
            Assert.Equal("150.0", 150.0.ToString("0.0###########", System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void BlocksWithElevationAndNumberAttributes()
        {
            // Sources.ReadBlock puts the number attribute in front of the elevation: «21 151.30».
            var texts = new List<SourceText>
            {
                new SourceText("B1", SourceKind.Block, "151.30", "21 151.30", new XY(1010, 2210)),
                new SourceText("B2", SourceKind.Block, "151,45", "22 151,45", new XY(1020, 2210)),
                new SourceText("B3", SourceKind.Block, "152", "23 152", new XY(1030, 2210)),
            };
            var plan = ImportPlanner.Plan(texts, null, null, new ImportOptions(XyMode.Insertion, 2));
            PointNumbering.Assign(plan, 1);
            Assert.Equal(new[] { "B1", "B2" }, plan.Accepted.Select(i => i.Source.Handle));
            Assert.Equal(new[] { 151.30, 151.45 }, plan.Accepted.Select(i => i.Elevation.Value));
            Assert.Equal(new[] { "21", "22" }, plan.Accepted.Select(i => i.Number));
            var rejected = Assert.Single(plan.Rejected);
            Assert.Equal("B3", rejected.Source.Handle);
            Assert.Equal(ImportReason.IntegerOnly, rejected.Reason);
        }
    }
}
