using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Profiles.AutoCAD.UI;
using Profiles.Core;

// Run this executable on Windows (.NET Framework 4.7.2+) or under Mono/Xvfb.
// AutoCAD is not involved: the production grid is compiled into this harness.
internal static class Program
{
    private static readonly MethodInfo HeaderClick = typeof(SectionPointGrid).GetMethod(
        "OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo HeaderMenu = typeof(SectionPointGrid).GetField(
        "headerRoleMenu", BindingFlags.Instance | BindingFlags.NonPublic);
    private static int passed;

    [STAThread]
    private static int Main()
    {
        // Production actions display validation errors in modal dialogs. A regression
        // must fail the unattended run instead of leaving it waiting for a click.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try { Console.Error.WriteLine("FAIL: WinForms regression timed out after 120 seconds; a modal dialog or blocked UI may be waiting for input."); }
            finally { Environment.Exit(124); }
        }, null, TimeSpan.FromSeconds(120), System.Threading.Timeout.InfiniteTimeSpan);
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Run("popup survives close notifications and repeated opening", CloseAndReopen);
            Run("role actions target the clicked column and refresh menu state", AssignRoles);
            Run("reloading points refreshes roles without losing measurements", ReloadPoints);
            Run("disposing the grid releases both menus", DisposeMenus);
            Console.WriteLine("Passed: " + passed + ", failed: 0");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            Console.Error.WriteLine("Passed: " + passed + ", failed: 1");
            return 1;
        }
    }

    private static void Run(string name, Action<SectionPointGrid> test)
    {
        using (var form = new Form { ClientSize = new Size(900, 300), ShowInTaskbar = false })
        {
            var grid = new SectionPointGrid();
            grid.LoadPoints(Points());
            form.Controls.Add(grid);
            form.Show();
            Application.DoEvents();
            test(grid);
            form.Close();
            Application.DoEvents();
        }
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static List<SectionPoint> Points() => new List<SectionPoint>
    {
        new SectionPoint { Role = SectionPointRole.LeftBank, DistanceFromPrevious = 0, Existing = 150, Design = 151 },
        new SectionPoint { Role = SectionPointRole.Bottom, DistanceFromPrevious = 2, Existing = 149, Design = 148 },
        new SectionPoint { Role = SectionPointRole.RightBank, DistanceFromPrevious = 3, Existing = 150, Design = 152 }
    };

    private static ContextMenuStrip Open(SectionPointGrid grid, int point)
    {
        HeaderClick.Invoke(grid, new object[] { new DataGridViewCellMouseEventArgs(
            point + 1, -1, 10, grid.ColumnHeadersHeight - 5,
            new MouseEventArgs(MouseButtons.Left, 1, 10, grid.ColumnHeadersHeight - 5, 0)) });
        Application.DoEvents();
        var menu = (ContextMenuStrip)HeaderMenu.GetValue(grid);
        Require(menu.Visible, "header menu did not open");
        Require(!menu.IsDisposed, "header menu was disposed while opening");
        Require(menu.Items.Count == 6, "header menu lost role or rename items");
        return menu;
    }

    private static void CloseAndReopen(SectionPointGrid grid)
    {
        var reasons = new[] { ToolStripDropDownCloseReason.Keyboard, ToolStripDropDownCloseReason.AppClicked,
            ToolStripDropDownCloseReason.AppFocusChange, ToolStripDropDownCloseReason.CloseCalled,
            ToolStripDropDownCloseReason.ItemClicked };
        var original = Open(grid, 0);
        int closed = 0;
        original.Closed += (s, e) =>
        {
            closed++;
            Require(!original.IsDisposed, "menu was disposed inside Closed, before WinForms finished closing it");
            Require(original.Items.Count == 6, "Closed disposed the popup's items");
        };
        for (int round = 0; round < 3; round++)
            foreach (var reason in reasons)
            {
                var menu = closed == 0 ? original : Open(grid, round % 3);
                Require(ReferenceEquals(original, menu), "repeated clicks allocated a different popup");
                menu.Close(reason);
                Application.DoEvents();
                Require(!menu.Visible && !menu.IsDisposed, "closed popup cannot be reused");
            }
        Require(closed == 15, "not all popup close paths ran");
    }

    private static void AssignRoles(SectionPointGrid grid)
    {
        var before = grid.ReadPoints();
        int changes = 0;
        grid.PointsChanged += (s, e) => changes++;
        var menu = Open(grid, 2);
        var left = (ToolStripMenuItem)menu.Items[0];
        Require(left.Text.Contains("№ 1"), "role transfer caption did not identify the previous column");
        Require(((ToolStripMenuItem)menu.Items[2]).Checked, "current right-bank role is not checked");
        left.PerformClick();
        menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        Application.DoEvents();
        var after = grid.ReadPoints();
        Require(after[2].Role == SectionPointRole.LeftBank && after[0].Role == SectionPointRole.Regular,
            "role was not transferred to the clicked column");
        Require(after.Count(point => point.Role == SectionPointRole.LeftBank) == 1, "role assignment introduced a duplicate bank");
        Require(changes == 1, "role assignment did not emit exactly one change");
        MeasurementsEqual(before, after);

        menu = Open(grid, 2);
        Require(((ToolStripMenuItem)menu.Items[0]).Checked, "reopened menu has stale role checks");
        Require(!menu.Items[0].Text.Contains("перенести"), "reopened menu has a stale transfer caption");
        menu.Close();
        menu = Open(grid, 0);
        ((ToolStripMenuItem)menu.Items[1]).PerformClick();
        menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        after = grid.ReadPoints();
        Require(after[0].Role == SectionPointRole.Bottom && after[1].Role == SectionPointRole.Regular,
            "reused menu applied its action to a stale column");
        Require(changes == 2, "reopening the menu accumulated action handlers");
        MeasurementsEqual(before, after);
    }

    private static void ReloadPoints(SectionPointGrid grid)
    {
        var original = Open(grid, 0);
        original.Close();
        var points = Points();
        points.Add(new SectionPoint { Role = SectionPointRole.Regular, DistanceFromPrevious = 4, Existing = 153 });
        grid.LoadPoints(points);
        var menu = Open(grid, 3);
        Require(ReferenceEquals(original, menu), "loading points replaced the popup");
        Require(((ToolStripMenuItem)menu.Items[3]).Checked, "new point did not receive current menu state");
        ((ToolStripMenuItem)menu.Items[2]).PerformClick();
        menu.Close(ToolStripDropDownCloseReason.ItemClicked);
        var after = grid.ReadPoints();
        Require(after[3].Role == SectionPointRole.RightBank && after[2].Role == SectionPointRole.Regular,
            "menu did not act on the newly loaded column");
        MeasurementsEqual(points, after);
    }

    private static void DisposeMenus(SectionPointGrid grid)
    {
        var header = Open(grid, 1);
        var context = grid.ContextMenuStrip;
        var headerItems = header.Items.Cast<ToolStripItem>().ToArray();
        var contextItems = context.Items.Cast<ToolStripItem>().ToArray();
        grid.Dispose();
        Application.DoEvents();
        Require(header.IsDisposed && context.IsDisposed, "grid disposal leaked an owned popup");
        Require(headerItems.All(item => item.IsDisposed) && contextItems.All(item => item.IsDisposed),
            "grid disposal leaked menu items");
        grid.Dispose();
    }

    private static void MeasurementsEqual(IList<SectionPoint> expected, IList<SectionPoint> actual)
    {
        Require(expected.Count == actual.Count, "menu action changed point count");
        for (int i = 0; i < expected.Count; i++)
            Require(expected[i].Existing == actual[i].Existing && expected[i].Design == actual[i].Design &&
                expected[i].DistanceFromPrevious == actual[i].DistanceFromPrevious,
                "menu action changed measurements for point " + i);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
