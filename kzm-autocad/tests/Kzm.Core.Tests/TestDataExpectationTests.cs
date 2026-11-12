using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>The same texts and markers as examples/kzm-test-data.lsp; expectations of VALIDATION.md.</summary>
    public class TestDataExpectationTests
    {
        private const double Bx = 1000, By = 2000;

        private static List<SourceText> Texts() => new List<SourceText>
        {
            ImportPlannerTests.Text("A", "145,32", Bx + 10, By),
            ImportPlannerTests.Text("B", "145.32", Bx + 20, By),
            ImportPlannerTests.Text("C", @"{\fArial|b0|i0|c204|p34;\H1.2x;145}{\C1;,32}", Bx + 30, By, SourceKind.MText),
            ImportPlannerTests.Text("D", "12", Bx + 40, By),
            ImportPlannerTests.Text("E", @"145,40\P146,10", Bx + 50, By, SourceKind.MText),
            ImportPlannerTests.Text("F", "147,05", Bx + 60, By),
            ImportPlannerTests.Text("G", "t.15 148,00", Bx + 70, By),
            ImportPlannerTests.Text("H1", "148,50", Bx + 80, By),
            ImportPlannerTests.Text("H2", "148,51", Bx + 80.005, By),
        };

        private static List<MarkerCandidate> Markers() => new[] { 9.5, 19.5, 29.5, 39.5, 49.5, 69.5, 79.5 }
            .Select((x, i) => new MarkerCandidate("M" + i, new XY(Bx + x, By))).ToList();

        [Fact]
        public void MarkerModeGivesFourAcceptedOneRejectedFourDisputed()
        {
            var plan = ImportPlanner.Plan(Texts(), Markers(), null, new ImportOptions(XyMode.Marker, 2));
            Assert.Equal(new[] { "A", "B", "C", "G" }, plan.Accepted.Select(i => i.Source.Handle));
            Assert.Equal(new[] { "D" }, plan.Rejected.Select(i => i.Source.Handle));
            Assert.Equal(new[] { "E", "F", "H1", "H2" }, plan.Disputed.Select(i => i.Source.Handle));
            Assert.All(plan.Accepted, item => Assert.Equal(0.5, item.MarkerDistance.Value, 6));
            Assert.Equal(ImportReason.IntegerOnly, plan.Items[3].Reason);
            Assert.Equal(ImportReason.MultipleNumbers, plan.Items[4].Reason);
            Assert.Equal(ImportReason.MarkerShared, plan.Items[5].Reason);
            Assert.Equal(ImportReason.MarkerShared, plan.Items[7].Reason);

            // VALIDATION 5.6–5.7: XY for F by hand, elevation for E typed — six points.
            ImportPlanner.ApplyOverrides(plan, new Dictionary<string, ImportOverride>
            {
                ["F"] = new ImportOverride { Position = new XY(Bx + 59.5, By) },
                ["E"] = new ImportOverride { Elevation = 146.10 },
            });
            Assert.Equal(6, plan.Accepted.Count());
            Assert.Empty(ImportPlanner.FindDuplicates(plan, null));
        }

        [Fact]
        public void InsertionModeGivesSevenAcceptedAndOneClosePair()
        {
            var plan = ImportPlanner.Plan(Texts(), null, null, new ImportOptions(XyMode.Insertion, 2));
            Assert.Equal(7, plan.Accepted.Count());
            Assert.Single(plan.Rejected);
            Assert.Single(plan.Disputed);
            var pair = Assert.Single(ImportPlanner.FindDuplicates(plan, null));
            Assert.Equal(0.005, pair.Distance, 6);
        }
    }
}
