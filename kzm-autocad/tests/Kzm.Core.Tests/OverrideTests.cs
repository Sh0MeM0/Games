using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class OverrideTests
    {
        private static SourceText Text(string handle, string value, double x, double y) => ImportPlannerTests.Text(handle, value, x, y);

        private static ImportPlan Plan(XyMode mode, params SourceText[] texts)
        {
            var markers = new[] { new MarkerCandidate("M1", new XY(9.5, 20)) };
            return ImportPlanner.Plan(texts, markers, null, new ImportOptions(mode, 2));
        }

        private static Dictionary<string, ImportOverride> One(string handle, ImportOverride decision) =>
            new Dictionary<string, ImportOverride> { [handle] = decision };

        [Fact]
        public void ElevationTypedForTwoNumbersInInsertionModeAcceptsTheRow()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "145,32 146,10", 10, 20));
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 146.10 }));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(10, 20), item.Position);
            Assert.Equal(ImportReason.MultipleNumbers, item.ResolvedFrom);
        }

        [Fact]
        public void TwoNumbersNextToTheirOwnMarkerNeedOnlyTheElevation()
        {
            var plan = Plan(XyMode.Marker, Text("A1", "145,32 146,10", 10, 20));
            var item = plan.Items.Single();
            Assert.Equal(ImportStatus.Disputed, item.Status);
            Assert.Equal("M1", item.MarkerHandle);
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 146.10 }));
            Assert.Equal(ImportStatus.Accepted, item.Status);
            Assert.Equal(new XY(9.5, 20), item.Position);
        }

        [Fact]
        public void ElevationWithoutAnyPositionStaysDisputedAndSaysWhatIsMissing()
        {
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32 146,10", 10, 20) }, new MarkerCandidate[0], null,
                new ImportOptions(XyMode.Marker, 2));
            var item = plan.Items.Single();
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 146.10 }));
            Assert.Equal(ImportStatus.Disputed, item.Status);
            Assert.Equal(ImportReason.ManualIncomplete, item.Reason);
            Assert.Contains("укажите положение", item.Message);
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 146.10, Position = new XY(9, 20) }));
            Assert.Equal(ImportStatus.Accepted, item.Status);
        }

        [Fact]
        public void PickedPositionResolvesATextWithoutMarker()
        {
            var plan = Plan(XyMode.Marker, Text("F", "147,05", 60, 20), Text("G", "148,00", 10, 20));
            Assert.Equal(ImportReason.MarkerShared, plan.Items[0].Reason);
            ImportPlanner.ApplyOverrides(plan, One("F", new ImportOverride { Position = new XY(59.5, 20), PositionMode = XyMode.Manual }));
            Assert.Equal(ImportStatus.Accepted, plan.Items[0].Status);
            Assert.Equal(147.05, plan.Items[0].Elevation.Value, 10);
            Assert.Equal(XyMode.Manual, plan.Items[0].PositionMode);
        }

        [Fact]
        public void RejectedTextCanBeTakenByTypingTheElevation()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "145", 10, 20));
            Assert.Equal(ImportReason.IntegerOnly, plan.Items.Single().Reason);
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 145.0 }));
            Assert.Equal(ImportStatus.Accepted, plan.Items.Single().Status);
            Assert.Equal(ImportReason.IntegerOnly, plan.Items.Single().ResolvedFrom);
        }

        [Fact]
        public void UntickedAcceptedRowIsExcluded()
        {
            var plan = Plan(XyMode.Insertion, Text("A1", "145,32", 10, 20));
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Exclude = true }));
            Assert.Equal(ImportStatus.Skipped, plan.Items.Single().Status);
            Assert.Equal(ImportReason.ManualSkipped, plan.Items.Single().Reason);
            Assert.Empty(plan.Accepted);
        }

        [Fact]
        public void AlreadyImportedTextIgnoresOverrides()
        {
            var existing = new[] { new ExistingPoint("P1", new XY(10, 20), 145.32, "A1") };
            var plan = ImportPlanner.Plan(new[] { Text("A1", "145,32", 10, 20) }, null, existing, new ImportOptions(XyMode.Insertion, 2));
            ImportPlanner.ApplyOverrides(plan, One("A1", new ImportOverride { Elevation = 150 }));
            Assert.Equal(ImportStatus.Skipped, plan.Items.Single().Status);
        }
    }
}
