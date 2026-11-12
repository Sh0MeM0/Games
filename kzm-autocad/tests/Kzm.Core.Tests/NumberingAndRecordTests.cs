using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class NumberingAndRecordTests
    {
        private static SourceText Text(string handle, string value, double x, double y) => ImportPlannerTests.Text(handle, value, x, y);

        [Fact]
        public void NumberFromTheSameTextFromANeighbourTextOrInOrder()
        {
            var texts = new[]
            {
                Text("A", "т.15 145,32", 10, 20),
                Text("B", "146,10", 30, 20), Text("NB", "7", 29.2, 21),
                Text("C", "147,00", 50, 20),
            };
            var markers = new[] { new MarkerCandidate("MA", new XY(9.5, 20)), new MarkerCandidate("MB", new XY(29.5, 20)), new MarkerCandidate("MC", new XY(49.5, 20)) };
            var plan = ImportPlanner.Plan(texts, markers, null, new ImportOptions(XyMode.Marker, 2));
            PointNumbering.Assign(plan, 101, markers);
            Assert.Equal("15", plan.Items[0].Number);
            Assert.Equal("7", plan.Items[1].Number);
            Assert.Same(plan.Items[1], plan.Items[2].NumberFor);
            Assert.Equal("101", plan.Items[3].Number);
        }

        [Fact]
        public void NumberTextAtAMarkerWithoutElevationIsNotGivenToAFarPoint()
        {
            // Like KZM_TEST_DATA: «12» stands at its own marker, which has no elevation text.
            var texts = new[] { Text("C", "145,32", 30, 20), Text("D", "12", 40, 20) };
            var markers = new[] { new MarkerCandidate("MC", new XY(29.5, 20)), new MarkerCandidate("MD", new XY(39.5, 20)) };
            var plan = ImportPlanner.Plan(texts, markers, null, new ImportOptions(XyMode.Marker, 2));
            PointNumbering.Assign(plan, 1, markers);
            Assert.Equal("1", plan.Items[0].Number);
            Assert.Null(plan.Items[1].NumberFor);
        }

        [Fact]
        public void InsertionModeTakesANumberOnlyWithinFiveTextHeights()
        {
            SourceText T(string h, string v, double x, double y) =>
                new SourceText(h, SourceKind.Text, v, v, new XY(x, y), "Standard", 1.0);
            var texts = new[] { T("A", "145,32", 0, 0), T("NA", "3", 0.5, 1.2), T("B", "146,00", 50, 0), T("NB", "4", 50, 9) };
            var plan = ImportPlanner.Plan(texts, null, null, new ImportOptions(XyMode.Insertion, 2));
            PointNumbering.Assign(plan, 10);
            Assert.Equal("3", plan.Items[0].Number);
            Assert.Equal("10", plan.Items[2].Number); // «4» is 9 m away: more than 5 text heights
        }

        [Fact]
        public void NextNumberFollowsTheLargestNumeric()
        {
            Assert.Equal(1, PointNumbering.Next(new string[0]));
            Assert.Equal(16, PointNumbering.Next(new[] { "3", "15", "т.2", "" }));
        }

        [Fact]
        public void SurfaceRecordRoundTripKeepsAppearance()
        {
            var surface = new SurfaceInfo
            {
                Id = "S1", Name = "Факт", Kind = SurfaceKind.Fact, Layer = "КЗМ_ТОЧКИ_ФАКТ", BoundaryHandle = "2A",
                ShowTriangles = true,
                Appearance = new PointAppearance { TextStyle = "GOST", TextHeight = 1.25, ElevationColor = "ACI:1", NumberColor = "RGB:0,128,255", ShowNumber = false },
            };
            var map = SurfaceRecord.Encode(surface).ToDictionary(p => p.Key, p => p.Value);
            var decoded = SurfaceRecord.Decode("S1", map);
            Assert.Equal("2A", decoded.BoundaryHandle);
            Assert.True(decoded.ShowTriangles);
            Assert.Equal("GOST", decoded.Appearance.TextStyle);
            Assert.Equal(1.25, decoded.Appearance.TextHeight);
            Assert.Equal(0.625, decoded.Appearance.EffectiveSignSize, 9);
            Assert.Equal("RGB:0,128,255", decoded.Appearance.NumberColor);
            Assert.False(decoded.Appearance.ShowNumber);
        }

        [Fact]
        public void OldSurfaceRecordWithoutAppearanceIsRead()
        {
            var map = new Dictionary<string, string> { ["name"] = "Факт", ["kind"] = "FACT", ["layer"] = "L", ["revision"] = "3" };
            var decoded = SurfaceRecord.Decode("S", map);
            Assert.False(decoded.Appearance.IsSet);
            Assert.Equal(PointAppearance.ByLayer, decoded.Appearance.ElevationColor);
            Assert.Equal(3, decoded.Revision);
        }

        [Fact]
        public void CartogramRecordRoundTrip()
        {
            var c = new CartogramInfo
            {
                Id = "C1", Name = "КЗМ-1", FactSurfaceId = "F", ProjectSurfaceId = "P", BoundaryHandle = "3B", Step = 20,
                OriginX = 5500000, OriginY = 4300000.5, AngleDegrees = 12.5, Columns = LabelScheme.Letters, Rows = LabelScheme.Numbers,
                Scale = 1000, VolumeDecimals = 1, Built = true, BuiltFingerprint = "abc", LastFill = 123.4, LastCut = 5,
            };
            var decoded = CartogramInfo.Decode("C1", CartogramInfo.Encode(c).ToDictionary(p => p.Key, p => p.Value));
            Assert.Equal(4300000.5, decoded.OriginY);
            Assert.Equal(LabelScheme.Letters, decoded.Columns);
            Assert.Equal(1000, decoded.Scale);
            Assert.Equal(1.0, decoded.UnitsPerMm);
            Assert.Equal("abc", decoded.BuiltFingerprint);
            Assert.Empty(decoded.Validate());
        }

        [Fact]
        public void NewCartogramShowsWhatIsMissing()
        {
            var problems = new CartogramInfo { Name = "К" }.Validate();
            Assert.Contains(problems, p => p.Contains("фактическая"));
            Assert.Contains(problems, p => p.Contains("граница"));
        }

        [Fact]
        public void FingerprintIgnoresFloatNoiseButNotChanges()
        {
            string a = new Fingerprint().Add("S").Add(145.32).ToString();
            string b = new Fingerprint().Add("S").Add(145.3200000000001).ToString();
            string c = new Fingerprint().Add("S").Add(145.33).ToString();
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }
    }
}
