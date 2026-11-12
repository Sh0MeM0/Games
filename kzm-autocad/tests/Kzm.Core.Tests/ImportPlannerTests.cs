using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class ImportPlannerTests
    {
        private static ElevationRules Rules() => new ElevationRules(KzmSettings.DefaultPattern, 140, 160, 2);

        private static SourceText Text(string handle, string value, double x, double y, SourceKind kind = SourceKind.Text) =>
            new SourceText(handle, kind, value, kind == SourceKind.MText ? TextCleaner.CleanMText(value) : TextCleaner.CleanText(value), new XY(x, y));

        [Fact]
        public void InsertionModeTakesTextPosition()
        {
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, null,
                new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(10, 20), item.Position);
            Assert.Equal(XyMode.Insertion, item.PositionMode);
        }

        [Fact]
        public void MarkerModeTakesNearestMarkerWithinRadius()
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(9, 19)), new MarkerCandidate("M2", new XY(13, 20)) };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, markers, null,
                new ImportOptions(Rules(), XyMode.Marker, 2.0, 0.01));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(9, 19), item.Position);
            Assert.Equal("M1", item.MarkerHandle);
        }

        [Fact]
        public void NoMarkerWithinRadiusIsDisputedButElevationIsKept()
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(30, 30)) };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, markers, null,
                new ImportOptions(Rules(), XyMode.Marker, 2.0, 0.01));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Disputed, item.Status);
            Assert.Equal(ImportReason.NoMarker, item.Reason);
            Assert.Equal(145.32, item.Elevation.Value, 10);
            Assert.Null(item.Position);
        }

        [Fact]
        public void EquidistantMarkersAreDisputed()
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(9, 20)), new MarkerCandidate("M2", new XY(11, 20)) };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, markers, null,
                new ImportOptions(Rules(), XyMode.Marker, 2.0, 0.01));
            Assert.Equal(ImportReason.MarkerTie, plan.Items.Single().Reason);
        }

        [Fact]
        public void MarkerNearestToTwoElevationsMakesBothDisputed()
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(10, 20)) };
            var texts = new[] { Text("A1", "145,32", 10.5, 20), Text("A2", "146,00", 9.2, 20), Text("A3", "12", 10, 20.1) };
            var plan = ImportPlanner.Plan(texts, markers, null, new ImportOptions(Rules(), XyMode.Marker, 2.0, 0.01));
            Assert.Equal(ImportReason.MarkerShared, plan.Items[0].Reason);
            Assert.Equal(ImportReason.MarkerShared, plan.Items[1].Reason);
            // A rejected point number does not compete for the marker.
            Assert.Equal(ImportReason.OutOfRange, plan.Items[2].Reason);
        }

        [Fact]
        public void TextAlreadyImportedIntoTheSurfaceIsSkipped()
        {
            var existing = new[] { new ExistingPoint("P1", new XY(10, 20), 145.32, "A1") };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, existing,
                new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Skipped, item.Status);
            Assert.Equal("P1", item.ExistingPointHandle);
        }

        [Fact]
        public void ManualResolutionAcceptsADisputedPoint()
        {
            var plan = ImportPlanner.Plan(new[] { Text("A1", @"145,32\P146,10", 10, 20, SourceKind.MText) }, null, null,
                new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            var item = plan.Items.Single();
            Assert.True(item.NeedsElevation);
            item.ResolveManually(146.10, new XY(11, 21), XyMode.Manual);
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(ImportReason.MultipleInRange, item.ResolvedFrom);
            Assert.Equal(XyMode.Manual, item.PositionMode);
        }

        [Fact]
        public void PointsCloserThanToleranceArePairedNotDropped()
        {
            var texts = new[] { Text("A1", "145,32", 10, 20), Text("A2", "145,35", 10.005, 20), Text("A3", "145,40", 10.02, 20) };
            var existing = new[] { new ExistingPoint("P1", new XY(10.019, 20), 145.40, "Z9") };
            var plan = ImportPlanner.Plan(texts, null, existing, new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            var pairs = ImportPlanner.FindDuplicates(plan, existing);
            Assert.Equal(3, plan.Accepted.Count());
            Assert.Equal(2, pairs.Count);
            Assert.Contains(pairs, pair => pair.First.Source.Handle == "A1" && pair.SecondNew?.Source.Handle == "A2");
            Assert.Contains(pairs, pair => pair.First.Source.Handle == "A3" && pair.SecondExisting?.Handle == "P1");
        }

        [Fact]
        public void DistanceEqualToToleranceIsNotADuplicate()
        {
            var pairs = DuplicateFinder.Find(new[] { new XY(0, 0), new XY(0.5, 0) }, new XY[0], 0.5);
            Assert.Empty(pairs);
        }

        [Fact]
        public void LargeSurveyCoordinatesWork()
        {
            var points = Enumerable.Range(0, 2000).Select(i => new XY(5_500_000 + i * 0.5, 4_300_000)).ToList();
            points.Add(new XY(5_500_000.004, 4_300_000));
            var pairs = DuplicateFinder.Find(points, new XY[0], 0.01);
            Assert.Single(pairs);
        }

        [Fact]
        public void SummaryListsEveryOutcome()
        {
            var texts = new[] { Text("A1", "145,32", 10, 20), Text("A2", "12", 0, 0), Text("A3", "145,32 146,10", 5, 5) };
            var plan = ImportPlanner.Plan(texts, null, null, new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            var lines = ImportSummary.Lines(plan, 0, 1);
            Assert.Contains("Найдено текстов: 3 (TEXT 3, MTEXT 0)", lines);
            Assert.Contains(lines, line => line.StartsWith("Отброшено: 1 (вне диапазона отметок — 1)"));
            Assert.Contains(lines, line => line.StartsWith("Спорных, не импортировано: 1 (несколько чисел в диапазоне — 1)"));
        }
    }

    public class SettingsAndElevationTests
    {
        [Fact]
        public void EmptySettingsListEveryMissingValue()
        {
            var problems = new KzmSettings().ValidateForImport();
            Assert.Contains(problems, p => p.Contains("диапазон"));
            Assert.Contains(problems, p => p.Contains("масштаб"));
            Assert.Contains(problems, p => p.Contains("высота подписи"));
            Assert.Contains(problems, p => p.Contains("размер знака"));
            Assert.DoesNotContain(problems, p => p.Contains("радиус"));
        }

        [Fact]
        public void SettingsRoundTrip()
        {
            var settings = new KzmSettings
            {
                MinElevation = 140.5, MaxElevation = 160, MarkerRadius = 1.25, ScaleDenominator = 500,
                LabelHeightMm = 2.5, PointSizeMm = 1.5, LastXyMode = XyMode.Insertion, LastSelectionMode = "LAYERS",
                NumberPattern = @"Н=(?<num>[0-9.,]+)",
            };
            var decoded = SettingsCodec.Decode(SettingsCodec.Encode(settings));
            Assert.Equal(140.5, decoded.MinElevation);
            Assert.Equal(160, decoded.MaxElevation);
            Assert.Equal(1.25, decoded.MarkerRadius);
            Assert.Equal(0.01, decoded.DuplicateTolerance);
            Assert.Equal(500, decoded.ScaleDenominator);
            Assert.Equal(0.5, decoded.UnitsPerPaperMm);
            Assert.Equal(XyMode.Insertion, decoded.LastXyMode);
            Assert.Equal(settings.NumberPattern, decoded.NumberPattern);
            Assert.Empty(decoded.ValidateForImport());
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
