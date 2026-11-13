using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Version 0.6: the grid ends at the boundary, crossings are labelled, nothing outside.</summary>
    public class BoundaryGridTests
    {
        private static CartogramResult TestCarto()
        {
            var points = new List<XY>();
            for (int i = 0; i <= 6; i++)
            for (int j = 0; j <= 4; j++)
                points.Add(new XY(1000 + 10 * i, 2100 + 10 * j));
            var fact = Tin.Build(points, points.Select(_ => 100.0).ToList());
            var project = Tin.Build(points, points.Select(p => 100.5 + 0.01 * (p.X - 1000)).ToList());
            var boundary = new List<XY> { new XY(1005, 2105), new XY(1045, 2105), new XY(1045, 2125), new XY(1005, 2125) };
            return CartogramCalculator.Compute(boundary, fact.ZAt, project.ZAt, new GridDefinition(20, new XY(1000, 2100), 0));
        }

        [Fact]
        public void CrossingsOfTheGridWithTheBoundary()
        {
            var r = TestCarto();
            var crossings = r.BoundaryPoints.Select(b => (Math.Round(b.Position.X, 6), Math.Round(b.Position.Y, 6))).OrderBy(p => p).ToList();
            Assert.Equal(new[] { (1005.0, 2120.0), (1020.0, 2105.0), (1020.0, 2125.0), (1040.0, 2105.0), (1040.0, 2125.0), (1045.0, 2120.0) }, crossings);
            // Values are interpolated in the surfaces: h = 0,50 + 0,01·(x − 1000).
            var left = r.BoundaryPoints.Single(b => Math.Abs(b.Position.X - 1005) < 1e-9);
            Assert.Equal(100.0, left.Fact.Value, 9);
            Assert.Equal(100.55, left.Project.Value, 9);
            Assert.Equal(0.55, left.Working.Value, 9);
            Assert.Equal(r.BoundaryPoints.Count, r.BoundaryPoints.Select(b => b.Key).Distinct().Count());
        }

        [Fact]
        public void OnlyNodesInsideAreLabelled()
        {
            var r = TestCarto();
            var inside = r.Nodes.Values.Where(n => n.Inside).Select(n => (n.Position.X, n.Position.Y)).OrderBy(p => p).ToList();
            Assert.Equal(new[] { (1020.0, 2120.0), (1040.0, 2120.0) }, inside);
            Assert.True(r.Nodes.Values.Count(n => n.Used) > inside.Count);
        }

        [Fact]
        public void GridLinesEndAtTheBoundary()
        {
            var r = TestCarto();
            Assert.Equal(3, r.GridSegments.Count);
            Assert.Equal(80, r.GridSegments.Sum(s => s.A.DistanceTo(s.B)), 9);
            var boundary = new List<XY> { new XY(1005, 2105), new XY(1045, 2105), new XY(1045, 2125), new XY(1005, 2125) };
            foreach (var (a, b) in r.GridSegments)
            {
                Assert.True(Geometry.Contains(boundary, a, 1e-6));
                Assert.True(Geometry.Contains(boundary, b, 1e-6));
            }
            // Volumes are unchanged by the drawing rules: mean h 0,75 m × 800 m².
            Assert.Equal(600, r.TotalFill, 6);
        }

        [Fact]
        public void BoundaryThroughANodeGivesNoExtraCrossing()
        {
            var points = new List<XY> { new XY(-10, -10), new XY(70, -10), new XY(70, 70), new XY(-10, 70) };
            var flat = Tin.Build(points, points.Select(_ => 1.0).ToList());
            var boundary = new List<XY> { new XY(0, 0), new XY(40, 0), new XY(40, 30), new XY(0, 30) };
            var r = CartogramCalculator.Compute(boundary, flat.ZAt, flat.ZAt, new GridDefinition(20, new XY(0, 0), 0));
            // Corners (0;0), (40;0) are nodes; the top edge v = 30 crosses u = 20; the side edges cross v = 20.
            var crossings = r.BoundaryPoints.Select(b => (b.Position.X, b.Position.Y)).OrderBy(p => p).ToList();
            Assert.Equal(new[] { (0.0, 30.0), (20.0, 30.0), (40.0, 30.0) }, crossings);
            Assert.All(r.Nodes.Values.Where(n => n.Position.Y <= 20), n => Assert.True(n.Inside));
        }
    }

    public class ProjectRecordTests
    {
        [Fact]
        public void ProjectOfOldRecordsIsProject1()
        {
            Assert.Equal(ProjectInfo.LegacyId, ProjectInfo.Of(null));
            Assert.Equal("abc", ProjectInfo.Of("abc"));
            var project = ProjectInfo.Decode("abc", ProjectInfo.Encode(new ProjectInfo { Id = "abc", Name = "Школа № 5", Created = "2026-11-13" })
                .ToDictionary(p => p.Key, p => p.Value));
            Assert.Equal("Школа № 5", project.Name);
            Assert.Equal(ProjectInfo.LegacyName, ProjectInfo.Decode(ProjectInfo.LegacyId, new Dictionary<string, string>()).Name);
        }

        [Fact]
        public void SurfaceAndCartogramKeepTheirProject()
        {
            var surface = new SurfaceInfo { Id = "s", Name = "Факт", Kind = SurfaceKind.Fact, Layer = "КЗМ_ТОЧКИ_ФАКТ", ProjectId = "p1" };
            var back = SurfaceRecord.Decode("s", SurfaceRecord.Encode(surface).ToDictionary(p => p.Key, p => p.Value));
            Assert.Equal("p1", back.ProjectId);
            var c = new CartogramInfo { Id = "c", Name = "К", ProjectId = "p1", TableGapMm = 15, GridLineWeight = 25 };
            var decoded = CartogramInfo.Decode("c", CartogramInfo.Encode(c).ToDictionary(p => p.Key, p => p.Value));
            Assert.Equal("p1", decoded.ProjectId);
            Assert.Equal(15, decoded.TableGapMm);
            Assert.Equal(25, decoded.GridLineWeight);
            Assert.DoesNotContain("prj", CartogramInfo.FingerprintPairs(decoded).Select(p => p.Key));
            Assert.Contains("table_gap", CartogramInfo.FingerprintPairs(decoded).Select(p => p.Key));
        }

        [Fact]
        public void OldRecordsGetDefaults()
        {
            var old = CartogramInfo.Decode("c", new Dictionary<string, string> { ["name"] = "К" });
            Assert.Null(old.ProjectId);
            Assert.Equal(10, old.TableGapMm);
            Assert.Equal(18, old.GridLineWeight);
            old.Built = true;
            Assert.True(old.OldDrawing);
            Assert.True(old.VolumesDrawn);
            Assert.Equal(4, old.HatchSpacingMm);
            Assert.Equal(13, old.HatchLineWeight);
            old.DrawVersion = CartogramInfo.CurrentDrawVersion;
            var back = CartogramInfo.Decode("c", CartogramInfo.Encode(old).ToDictionary(p => p.Key, p => p.Value));
            Assert.False(back.OldDrawing);
            Assert.DoesNotContain("draw_v", CartogramInfo.FingerprintPairs(back).Select(p => p.Key));
        }
    }
}

namespace Kzm.Core.Tests
{
    /// <summary>The numbers of VALIDATION.md for 0.6 (boundary by points, an elevation typed on the cartogram).</summary>
    public class Validation06Tests
    {
        private static Tin Fact()
        {
            var points = new List<XY>();
            for (int i = 0; i <= 6; i++)
            for (int j = 0; j <= 4; j++)
                points.Add(new XY(1000 + 10 * i, 2100 + 10 * j));
            return Tin.Build(points, points.Select(_ => 100.0).ToList());
        }

        private static readonly List<XY> Corners = new List<XY> { new XY(1005, 2105), new XY(1045, 2105), new XY(1045, 2125), new XY(1005, 2125) };

        [Fact]
        public void BoundaryByPointsAt100_50Gives400()
        {
            // The boundary drawn by points: its corners become project points (100,50) and a closed structure line.
            var project = Tin.Build(Corners, Corners.Select(_ => 100.5).ToList(), null, null, new[] { (0, 1), (1, 2), (2, 3), (3, 0) });
            var r = CartogramCalculator.Compute(Corners, Fact().ZAt, project.ZAt, new GridDefinition(20, new XY(1000, 2100), 0));
            Assert.Equal(400, r.TotalFill, 6);
            Assert.All(r.BoundaryPoints, b => Assert.Equal(0.5, b.Working.Value, 9));
        }

        [Fact]
        public void ProjectElevationTypedAtANodeRaisesTheSurface()
        {
            // Project 100,50 at the corners; on the cartogram the node (1020; 2120) gets 101,00 — a new project point there.
            var points = Corners.Concat(new[] { new XY(1020, 2120) }).ToList();
            var z = new List<double> { 100.5, 100.5, 100.5, 100.5, 101.0 };
            var project = Tin.Build(points, z, null, null, new[] { (0, 1), (1, 2), (2, 3), (3, 0) });
            Assert.Equal(101.0, project.ZAt(new XY(1020, 2120)).Value, 9);
            var control = ControlVolume.Compute(Corners, Fact(), project);
            // 400 m³ of the plane + a pyramid of height 0,50 over 800 m²: 0,5 × 800 / 3 = 133,33 m³.
            Assert.Equal(400 + 0.5 * 800 / 3, control.Fill, 6);
            var r = CartogramCalculator.Compute(Corners, Fact().ZAt, project.ZAt, new GridDefinition(20, new XY(1000, 2100), 0));
            var node = r.Nodes.Values.Single(n => Math.Abs(n.Position.X - 1020) < 1e-9 && Math.Abs(n.Position.Y - 2120) < 1e-9);
            Assert.Equal(1.0, node.Working.Value, 9);
            // VALIDATION.md, step 6: the method of squares gives 516,33 m³ (columns 200,21 / 263,63 / 52,50), 17,0 m³
            // (3,19 %) less than the exact control volume. Version 0.9 counts with the marks as written (rounded to 0,01 m):
            // where a square centre takes its mark from the surface, 100,6667 is counted as 100,67 (0.8: 515,83 m³).
            Assert.Equal(516.3333333333, r.TotalFill, 6);
            Assert.Equal(new[] { 200.2083333333, 263.625, 52.5 }, r.ColumnTotals.OrderBy(t => t.Key).Select(t => Math.Round(t.Value.Fill, 6)).ToArray(), new ToleranceComparer(1e-6));
            var (cubic, percent) = ControlVolumeResult.Difference(r.TotalFill, control.Fill);
            Assert.Equal(-17.0, cubic, 6);
            Assert.Equal(-3.1875, percent.Value, 6);
        }
    }
}

namespace Kzm.Core.Tests
{
    internal sealed class ToleranceComparer : IEqualityComparer<double>
    {
        private readonly double tolerance;
        internal ToleranceComparer(double tolerance) { this.tolerance = tolerance; }
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => 0;
    }
}

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 18: the three labels of a node stay consistent while the user edits them.</summary>
    public class NodeLabelsTests
    {
        [Fact]
        public void ProjectChangedRecalculatesWorking()
        {
            var (w, p, f) = NodeLabels.Reconcile("+0,24", "176,82", "176,46", "+0,24", "176,70", "176,46");
            Assert.Equal(("+0,36", "176,82", "176,46"), (w, p, f));
        }

        [Fact]
        public void FactAndProjectChangedTogether()
        {
            // The example of the remark: 176,70 → 176,82 and 176,46 → 176,40 give +0,42.
            var result = NodeLabels.Reconcile("+0,24", "176,82", "176,40", "+0,24", "176,70", "176,46");
            Assert.Equal(("+0,42", "176,82", "176,40"), result);
        }

        [Fact]
        public void WorkingChangedMovesProject()
        {
            var result = NodeLabels.Reconcile("-0,10", "176,70", "176,46", "+0,24", "176,70", "176,46");
            Assert.Equal(("-0,10", "176,36", "176,46"), result);
        }

        [Fact]
        public void DotAndCommaAreAccepted()
        {
            var result = NodeLabels.Reconcile("+0,24", "176.8", "176,46", "+0,24", "176,70", "176,46");
            Assert.Equal(("+0,34", "176,80", "176,46"), result);
        }

        [Fact]
        public void UnreadableTextIsLeftAsTyped()
        {
            var result = NodeLabels.Reconcile("+0,24", "abc", "176,46", "+0,24", "176,70", "176,46");
            Assert.Equal(("+0,24", "abc", "176,46"), result);
        }
    }
}
