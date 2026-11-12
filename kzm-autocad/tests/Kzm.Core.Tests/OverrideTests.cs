using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class OverrideTests
    {
        private static ElevationRules Rules() => new ElevationRules(KzmSettings.DefaultPattern, 140, 160, 2);

        private static SourceText Text(string handle, string value, double x, double y) =>
            new SourceText(handle, SourceKind.Text, value, TextCleaner.CleanText(value), new XY(x, y));

        private static ImportPlan Plan(XyMode mode, params SourceText[] texts)
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(9.5, 20)) };
            return ImportPlanner.Plan(texts, markers, null, new ImportOptions(Rules(), mode, mode == XyMode.Marker ? 1.0 : (double?)null, 0.01));
        }

        [Fact]
        public void ElevationTypedForTwoNumbersInInsertionModeAcceptsTheRow()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "145,32 146,10", 10, 20));
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride> { ["A1"] = new ImportOverride { Elevation = 146.10 } });
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(10, 20), item.Position);
            Assert.Equal(ImportReason.MultipleInRange, item.ResolvedFrom);
        }

        [Fact]
        public void ElevationWithoutPositionInMarkerModeStaysDisputedAndSaysWhatIsMissing()
        {
            var plan = Plan(XyMode.Marker, Text("A1", "145,32 146,10", 10, 20));
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride> { ["A1"] = new ImportOverride { Elevation = 146.10 } });
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Disputed, item.Status);
            Assert.Equal(ImportReason.ManualIncomplete, item.Reason);
            Assert.Contains("укажите положение", item.Message);

            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride>
            {
                ["A1"] = new ImportOverride { Elevation = 146.10, Position = new XY(9.5, 20), PositionMode = XyMode.Manual },
            });
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(ImportReason.MultipleInRange, item.ResolvedFrom);
        }

        [Fact]
        public void PickedPositionResolvesAMissingMarker()
        {
            var plan = Plan(XyMode.Marker, Text("A1", "147,05", 60, 20));
            Assert.Equal(ImportReason.NoMarker, plan.Items.Single().Reason);
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride>
            {
                ["A1"] = new ImportOverride { Position = new XY(59.5, 20), PositionMode = XyMode.Manual },
            });
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(147.05, item.Elevation.Value, 10);
            Assert.Equal(XyMode.Manual, item.PositionMode);
        }

        [Fact]
        public void RejectedTextCanBeTakenByTypingTheElevation()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "12", 10, 20));
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride> { ["A1"] = new ImportOverride { Elevation = 145.12 } });
            Assert.Equal(ImportStatus.Accepted, plan.Items.Single().Status);
            Assert.Equal(ImportReason.OutOfRange, plan.Items.Single().ResolvedFrom);
        }

        [Fact]
        public void UntickedAcceptedRowIsExcluded()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "145,32", 10, 20));
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride> { ["A1"] = new ImportOverride { Exclude = true } });
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Skipped, item.Status);
            Assert.Equal(ImportReason.ManualSkipped, item.Reason);
            Assert.Empty(plan.Accepted);
        }

        [Fact]
        public void AlreadyImportedTextIgnoresOverrides()
        {
            var existing = new[] { new ExistingPoint("P1", new XY(10, 20), 145.32, "A1") };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, existing,
                new ImportOptions(Rules(), XyMode.Insertion, null, 0.01));
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride> { ["A1"] = new ImportOverride { Elevation = 150 } });
            Assert.Equal(ImportStatus.Skipped, plan.Items.Single().Status);
        }
    }
}
