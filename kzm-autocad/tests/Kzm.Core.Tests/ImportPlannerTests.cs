using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class ImportPlannerTests
    {
        private static ImportOptions Options(XyMode mode) => new ImportOptions(mode, 2);

        internal static SourceText Text(string handle, string value, double x, double y, SourceKind kind = SourceKind.Text) =>
            new SourceText(handle, kind, value, kind == SourceKind.MText ? TextCleaner.CleanMText(value) : TextCleaner.CleanText(value), new XY(x, y));

        private static MarkerCandidate Marker(string handle, double x, double y) => new MarkerCandidate(handle, new XY(x, y));

        [Fact]
        public void InsertionModeTakesTextPosition()
        {
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, null, Options(XyMode.Insertion));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(10, 20), item.Position);
            Assert.Equal(XyMode.Insertion, item.PositionMode);
        }

        [Fact]
        public void MutuallyNearestMarkerIsTakenWithoutRadius()
        {
            var markers = new[] { Marker("M1", 9.5, 20), Marker("M2", 19.5, 20) };
            var texts = new[] { Text("A1", "145,32", 10, 20), Text("A2", "146,00", 20, 20) };
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.All(plan.Items, item => Assert.Equal(ImportStatus.Accepted, item.Status));
            Assert.Equal("M1", plan.Items[0].MarkerHandle);
            Assert.Equal("M2", plan.Items[1].MarkerHandle);
            Assert.Equal(0.5, plan.Items[0].MarkerDistance.Value, 10);
        }

        [Fact]
        public void TextWithoutOwnMarkerDoesNotStealTheNeighbours()
        {
            // F has no marker; the nearest one belongs to G, which is closer to it.
            var markers = new[] { Marker("MG", 69.5, 20) };
            var texts = new[] { Text("F", "147,05", 60, 20), Text("G", "148,00", 70, 20) };
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.Equal(ImportReason.MarkerShared, plan.Items[0].Reason);
            Assert.Equal(ImportStatus.Disputed, plan.Items[0].Status);
            Assert.Equal(147.05, plan.Items[0].Elevation.Value, 10);
            Assert.Equal(ImportStatus.Accepted, plan.Items[1].Status);
        }

        [Fact]
        public void NoMarkersAtAllIsDisputed()
        {
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, new MarkerCandidate[0], null, Options(XyMode.Marker));
            Assert.Equal(ImportReason.NoMarker, plan.Items.Single().Reason);
        }

        [Fact]
        public void EquidistantMarkersAreDisputed()
        {
            var markers = new[] { Marker("M1", 9, 20), Marker("M2", 11, 20) };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, markers, null, Options(XyMode.Marker));
            Assert.Equal(ImportReason.MarkerTie, plan.Items.Single().Reason);
        }

        [Fact]
        public void TwoTextsAtOneMarkerAreBothDisputedWhenEquidistant()
        {
            var markers = new[] { Marker("M1", 80, 20) };
            var texts = new[] { Text("H1", "148,50", 80.5, 20), Text("H2", "148,51", 79.5, 20) };
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.All(plan.Items, item => Assert.Equal(ImportReason.MarkerShared, item.Reason));
        }

        [Fact]
        public void PointNumbersDoNotCompeteForMarkers()
        {
            // The point number «12» is closer to the marker than the elevation text.
            var markers = new[] { Marker("M1", 40, 20) };
            var texts = new[] { Text("N", "12", 40.2, 20), Text("E", "145,10", 40.8, 20) };
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.Equal(ImportReason.IntegerOnly, plan.Items[0].Reason);
            Assert.Equal(ImportStatus.Accepted, plan.Items[1].Status);
            Assert.Equal("M1", plan.Items[1].MarkerHandle);
        }

        [Fact]
        public void FarMarkerIsAcceptedButFlagged()
        {
            var markers = new[] { Marker("M1", 9.5, 20), Marker("M2", 19.5, 20), Marker("M3", 29.5, 20), Marker("M4", 46, 20) };
            var texts = new[] { Text("A", "145,1", 10, 20), Text("B", "145,2", 20, 20), Text("C", "145,3", 30, 20), Text("D", "145,4", 40, 20) };
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.All(plan.Items, item => Assert.Equal(ImportStatus.Accepted, item.Status));
            Assert.Null(plan.Items[0].Warning);
            Assert.Contains("маркер далеко", plan.Items[3].Warning);
        }

        [Fact]
        public void TextAlreadyImportedIntoTheSurfaceIsSkipped()
        {
            var existing = new[] { new ExistingPoint("P1", new XY(10, 20), 145.32, "A1") };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, existing, Options(XyMode.Insertion));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Skipped, item.Status);
            Assert.Equal("P1", item.ExistingPointHandle);
        }

        [Fact]
        public void PointsCloserThanToleranceArePairedNotDropped()
        {
            var texts = new[] { Text("A1", "145,32", 10, 20), Text("A2", "145,35", 10.005, 20), Text("A3", "145,40", 10.02, 20) };
            var existing = new[] { new ExistingPoint("P1", new XY(10.019, 20), 145.40, "Z9") };
            var plan = ImportPlanner.Plan(texts, null, existing, Options(XyMode.Insertion));
            var pairs = ImportPlanner.FindDuplicates(plan, existing);
            Assert.Equal(3, plan.Accepted.Count());
            Assert.Equal(2, pairs.Count);
            Assert.Contains(pairs, pair => pair.First.Source.Handle == "A1" && pair.SecondNew?.Source.Handle == "A2");
            Assert.Contains(pairs, pair => pair.First.Source.Handle == "A3" && pair.SecondExisting?.Handle == "P1");
        }

        [Fact]
        public void DistanceEqualToToleranceIsNotADuplicate()
        {
            Assert.Empty(DuplicateFinder.Find(new[] { new XY(0, 0), new XY(0.5, 0) }, new XY[0], 0.5));
        }

        [Fact]
        public void LargeSurveyCoordinatesWork()
        {
            var points = Enumerable.Range(0, 2000).Select(i => new XY(5_500_000 + i * 0.5, 4_300_000)).ToList();
            points.Add(new XY(5_500_000.004, 4_300_000));
            Assert.Single(DuplicateFinder.Find(points, new XY[0], 0.01));
        }

        [Fact]
        public void MarkerMatchingScalesToLargeSurveys()
        {
            var markers = new List<MarkerCandidate>();
            var texts = new List<SourceText>();
            for (int i = 0; i < 100; i++)
            for (int j = 0; j < 100; j++)
            {
                double x = 5_500_000 + i * 20, y = 4_300_000 + j * 20;
                markers.Add(Marker("M" + i + "_" + j, x, y));
                texts.Add(Text("T" + i + "_" + j, "145,32", x + 0.6, y + 0.3));
            }
            var plan = ImportPlanner.Plan(texts, markers, null, Options(XyMode.Marker));
            Assert.Equal(10_000, plan.Accepted.Count());
            Assert.All(plan.Items, item => Assert.Equal("M" + item.Source.Handle.Substring(1), item.MarkerHandle));
        }

        [Fact]
        public void SummaryListsEveryOutcome()
        {
            var texts = new[] { Text("A1", "145,32", 10, 20), Text("A2", "12", 0, 0), Text("A3", "145,32 146,10", 5, 5) };
            var plan = ImportPlanner.Plan(texts, null, null, Options(XyMode.Insertion));
            var lines = ImportSummary.Lines(plan, 0, 1);
            Assert.Contains("Найдено текстов: 3 (TEXT 3, MTEXT 0)", lines);
            Assert.Contains(lines, line => line.StartsWith("Отброшено: 1 (только целое число — не отметка — 1)"));
            Assert.Contains(lines, line => line.StartsWith("Спорных, не импортировано: 1 (несколько дробных чисел — 1)"));
        }
    }

    public class SettingsAndElevationTests
    {
        [Fact]
        public void SettingsRoundTrip()
        {
            var settings = new KzmSettings { Scale = 1000, Decimals = 3, LastXyMode = XyMode.Insertion, LastSelectionMode = "LAYERS" };
            var decoded = SettingsCodec.Decode(SettingsCodec.Encode(settings));
            Assert.Equal(1000, decoded.Scale);
            Assert.Equal(3, decoded.Decimals);
            Assert.Equal(XyMode.Insertion, decoded.LastXyMode);
            Assert.Equal("LAYERS", decoded.LastSelectionMode);
            Assert.Empty(decoded.Validate());
        }

        [Fact]
        public void SettingsOfOlderVersionsAreStillRead()
        {
            var old = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("version", "1"),
                new KeyValuePair<string, string>("pattern", "[0-9]+"),
                new KeyValuePair<string, string>("elev_min", "140"),
                new KeyValuePair<string, string>("label_height_mm", "2.5"),
                new KeyValuePair<string, string>("scale", "1000"),
            };
            Assert.Equal(1000, SettingsCodec.Decode(old).Scale);
        }

        [Fact]
        public void NewerSettingsVersionIsNotOverwrittenSilently()
        {
            var values = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("version", "99") };
            Assert.Throws<KzmException>(() => SettingsCodec.Decode(values));
        }

        [Fact]
        public void AttributeEditChangesElevation()
        {
            var unchanged = PointElevation.Resolve(145.325, "145,33", 2);
            Assert.False(unchanged.EditedByUser);
            Assert.Equal(145.325, unchanged.Value);

            var edited = PointElevation.Resolve(145.325, "145.4", 2);
            Assert.True(edited.EditedByUser);
            Assert.Equal(145.4, edited.Value.Value, 10);

            var broken = PointElevation.Resolve(145.325, "абв", 2);
            Assert.False(broken.IsValid);
            Assert.Contains("абв", broken.Problem);
        }
    }
}
