using System;
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

namespace Kzm.Core.Tests
{
    /// <summary>The KZM_TEST_CARTO data of examples/kzm-test-data.lsp and the volume promised in VALIDATION.md.</summary>
    public class CartogramTestDataTests
    {
        [Fact]
        public void TestCartoGivesFourHundredCubicMetresOfFill()
        {
            const double bx = 1000, by = 2100;
            var fact = new System.Collections.Generic.List<XY>();
            var project = new System.Collections.Generic.List<XY>();
            for (int i = 0; i <= 6; i++)
            for (int j = 0; j <= 4; j++)
            {
                fact.Add(new XY(bx + i * 10, by + j * 10));
                project.Add(new XY(bx + i * 10 + 3, by + j * 10 + 3));
            }
            var factTin = Tin.Build(fact, fact.Select(_ => 100.00).ToList());
            var projectTin = Tin.Build(project, project.Select(_ => 100.50).ToList());
            var boundary = new System.Collections.Generic.List<XY> { new XY(bx + 5, by + 5), new XY(bx + 45, by + 5), new XY(bx + 45, by + 25), new XY(bx + 5, by + 25) };
            // Auto origin: multiples of the step (as CartogramService.GridFor does for angle 0).
            var grid = new GridDefinition(20, new XY(Math.Floor((bx + 5) / 20) * 20, Math.Floor((by + 5) / 20) * 20), 0);
            var result = CartogramCalculator.Compute(boundary, factTin.ZAt, projectTin.ZAt, grid);
            Assert.Equal(400, result.TotalFill, 6);
            Assert.Equal(0, result.TotalCut, 6);
            Assert.True(result.Balanced);
            Assert.All(result.Squares, s => Assert.True(s.Computed));
        }
    }
}

namespace Kzm.Core.Tests
{
    public class TestDataNumberingTests
    {
        [Fact]
        public void KzmTestDataNumbers()
        {
            const double bx = 1000, by = 2000;
            var texts = new System.Collections.Generic.List<SourceText>
            {
                ImportPlannerTests.Text("A", "145,32", bx + 10, by), ImportPlannerTests.Text("B", "145.32", bx + 20, by),
                ImportPlannerTests.Text("C", @"{\fArial|b0|i0|c204|p34;\H1.2x;145}{\C1;,32}", bx + 30, by, SourceKind.MText),
                ImportPlannerTests.Text("D", "12", bx + 40, by),
                ImportPlannerTests.Text("E", @"145,40\P146,10", bx + 50, by, SourceKind.MText),
                ImportPlannerTests.Text("F", "147,05", bx + 60, by), ImportPlannerTests.Text("G", "t.15 148,00", bx + 70, by),
                ImportPlannerTests.Text("H1", "148,50", bx + 80, by), ImportPlannerTests.Text("H2", "148,51", bx + 80.005, by),
            };
            var markers = new[] { 9.5, 19.5, 29.5, 39.5, 49.5, 69.5, 79.5 }.Select((x, i) => new MarkerCandidate("M" + i, new XY(bx + x, by))).ToList();
            var plan = ImportPlanner.Plan(texts, markers, null, new ImportOptions(XyMode.Marker, 2));
            PointNumbering.Assign(plan, 1, markers);
            Assert.Equal(new[] { "1", "2", "3", "15" }, plan.Accepted.Select(i => i.Number));
            Assert.Null(plan.Items[3].NumberFor);
        }
    }
}
