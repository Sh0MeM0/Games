using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Profiles.AutoCAD;
using Profiles.AutoCAD.UI;
using Profiles.Core;

internal static class FormWorkflowTests
{
    internal static int Passed;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        foreach (string name in new[] { "0+20", "20", "ПК 0+20" })
            Check("station name " + name + " transfers captured survey into longitudinal grid", () => NameAssigns(name));
        Check("ordinary survey name stays unassigned", OrdinaryName);
        Check("profile deletion keeps the surviving tab and persists its selection", DeleteProfile);
        Check("end station displays recalculating extrapolated elevations without saving anchors", EndStation);
        Check("drawn station shows interpolated bottom and slopes in the editor", InterpolatedDimensions);
        Check("row fill is explicit, preserves manual input and survives template switches", FillRow);
        Check("cross-section width dialog and elevation button fill inserted columns and persist", ApproximateCrossSection);
        Check("station picker assigns existing station without rounding its precision", StationPicker);
        Check("drainage collector shows its columns and title fields and keeps the outlet mark", DrainageCollector);
        Check("an empty profile switched to drainage takes the collector sheet scales", DrainageScales);
        Check("the inflow cell lists the drainage tabs, moves a junction and rebuilds the tributary", DrainageInflowList);
        Check("a new tributary created at a picket starts at the collector and locks its outlet", DrainageNewTributary);
    }

    private static void DrainageCollector()
    {
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, SampleProjects.Drainage()))
        {
            Prepare(form);
            var grid = Field<DataGridView>(form, "longGrid");
            // Mono applies DisplayIndex differently from .NET; check the set of columns, not their order.
            var visible = grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Select(column => column.Name).OrderBy(name => name).ToArray();
            Require(visible.SequenceEqual(new[] { "FillMissing", "Station", "Ground", "PeatDepth", "ProjectBottom", "PipeDescription", "PointName" }.OrderBy(name => name)),
                "drainage columns: " + string.Join(", ", visible));
            Require(grid.Columns["ProjectBottom"].HeaderText == "Дрена (проект), м", "drainage heading is missing");
            Require(grid.Columns["PipeDescription"].HeaderText == "Диаметр, мм", "diameter heading is missing");
            Require(grid.Columns["Station"].HeaderText == "ПК / станция, м", "station column is not the picket: " + grid.Columns["Station"].HeaderText);
            Require(Field<Label>(form, "titleCaption").Text == "Коллектор", "title caption is not the collector");
            Require(Field<TextBox>(form, "subtitle").Text == "к-л К-2 пк3+40", "receiver was not loaded");
            Require(Field<TextBox>(form, "outlet").Text == "144.58", "outlet mark was not loaded");
            Field<TextBox>(form, "outlet").Text = "144,55";
            Field<TextBox>(form, "subtitle").Text = "р. Каменка пк76+90";
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(result.Template == ProfileTemplate.Drainage, "drainage type was lost");
            Require(result.Settings.OutletElevation == 144.55, "outlet mark was not read");
            Require(result.Settings.Subtitle == "р. Каменка пк76+90", "receiver was not read");
            var texts = ProfileEngine.Build(result).Entities.OfType<DrawText>().Select(text => text.Text).ToList();
            Require(texts.Contains("144.55") && texts.Contains("р. Каменка пк76+90"), "edited marks are not drawn");
            // Returning to a channel restores its own headings.
            Field<ComboBox>(form, "templatePicker").SelectedIndex = 1;
            Call(form, "ApplyTemplateColumns");
            Require(grid.Columns["ProjectBottom"].HeaderText == "Дно проект, м", "channel heading was not restored");
            Require(Field<Label>(form, "titleCaption").Text == "Название", "channel title caption was not restored");
        }
    }

    private static void DrainageScales()
    {
        var empty = new ProfileProject
        {
            Kind = ProfileKind.Longitudinal, Template = ProfileTemplate.Reconstruction,
            Longitudinal = new List<LongitudinalPoint>(), Sections = new List<CrossSection>()
        };
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, empty))
        {
            Prepare(form);
            var picker = Field<ComboBox>(form, "templatePicker");
            picker.SelectedIndex = Enumerable.Range(0, picker.Items.Count).Single(index => picker.Items[index].ToString() == "Дренажный коллектор");
            Call(form, "TemplateSelectionChanged", form, EventArgs.Empty);
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(result.Template == ProfileTemplate.Drainage, "type did not change to drainage");
            Require(result.Settings.HorizontalScale == 2000 && result.Settings.VerticalScale == 100,
                "scales " + result.Settings.HorizontalScale + " / " + result.Settings.VerticalScale);
        }
    }

    private static DataGridViewRow Row(DataGridView grid, double station) =>
        grid.Rows.Cast<DataGridViewRow>().Single(row => !row.IsNewRow && Convert.ToDouble(row.Cells["Station"].Value) == station);

    private static List<ToolStripMenuItem> InflowItems(ProfilesForm form, DataGridViewRow row)
    {
        Call(form, "BuildInflowMenu", row);
        return Field<ContextMenuStrip>(form, "inflowMenu").Items.OfType<ToolStripMenuItem>().ToList();
    }

    private static void DrainageInflowList()
    {
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, SampleProjects.Drainage()))
        {
            Prepare(form);
            var grid = Field<DataGridView>(form, "longGrid");
            var items = InflowItems(form, Row(grid, 38));
            Require(items[0].Text == "Впадает в «К-р 3'» на ПК 0+38:" && !items[0].Enabled, "list heading: " + items[0].Text);
            var tributary = items.Single(item => item.Text.StartsWith("К-р 3-2'"));
            Require(tributary.Checked && tributary.Text == "К-р 3-2'", "linked tributary is not checked: " + tributary.Text);
            Require(items.Any(item => item.Text == "Новый коллектор, впадающий здесь…" && item.Enabled), "new tributary command is missing");
            Require(items.Single(item => item.Text == "Очистить").Enabled, "a filled cell cannot be cleared");
            // The same tributary chosen at 150 m: the junction moves after confirmation.
            items = InflowItems(form, Row(grid, 150));
            tributary = items.Single(item => item.Text.StartsWith("К-р 3-2'"));
            Require(!tributary.Checked && tributary.Text == "К-р 3-2'  — сейчас впадает в «К-р 3'» на ПК 0+38", "move note: " + tributary.Text);
            string asked = null;
            form.ConfirmInflowMove = question => { asked = question; return true; };
            tributary.PerformClick();
            Require(asked != null && asked.Contains("Перенести впадение сюда — на ПК 1+50"), "move was not confirmed: " + asked);
            Require(Convert.ToString(Row(grid, 150).Cells["PointName"].Value) == "3-2", "label was not written at 150 m");
            Require(Convert.ToString(Row(grid, 38).Cells["PointName"].Value) == "", "old junction kept its label");
            var saved = (ProfileProject)typeof(ProfilesForm).GetProperty("Project").GetValue(form);
            var rebuilt = saved.AdditionalProfiles.Single(profile => profile.Name == "К-р 3-2'");
            Require(rebuilt.Longitudinal[0].ProjectBottom == 146.27 && rebuilt.Longitudinal[0].Ground == 147.90,
                "outlet was not rebuilt: " + rebuilt.Longitudinal[0].ProjectBottom + " / " + rebuilt.Longitudinal[0].Ground);
            Require(Math.Abs(rebuilt.Longitudinal[1].ProjectBottom.Value - 146.62) < 1e-9, "the slope was not kept: " + rebuilt.Longitudinal[1].ProjectBottom);
            Require(rebuilt.Settings.Subtitle == "к-р 3 пк1+50 к-л К-2", "receiver line: " + rebuilt.Settings.Subtitle);
            string status = Field<Label>(form, "status").Text;
            Require(status.Contains("«К-р 3-2'» перестроен по «К-р 3'» на ПК 1+50"), "status: " + status);
            // Unchecking removes the junction only.
            InflowItems(form, Row(grid, 150)).Single(item => item.Text.StartsWith("К-р 3-2'")).PerformClick();
            Require(Convert.ToString(Row(grid, 150).Cells["PointName"].Value) == "", "label was not removed");
            saved = (ProfileProject)typeof(ProfilesForm).GetProperty("Project").GetValue(form);
            Require(DrainageNetwork.Links(saved).Count == 0, "the link remained");
            Require(saved.AdditionalProfiles.Single(profile => profile.Name == "К-р 3-2'").Longitudinal[0].ProjectBottom == 146.27, "unlinking changed elevations");
        }
    }

    private static void DrainageNewTributary()
    {
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, SampleProjects.Drainage()))
        {
            Prepare(form);
            var grid = Field<DataGridView>(form, "longGrid");
            string suggested = null;
            form.AskTributaryName = name => { suggested = name; return name; };
            InflowItems(form, Row(grid, 72)).Single(item => item.Text == "Новый коллектор, впадающий здесь…").PerformClick();
            Require(suggested == "К-р 3-1'", "suggested name: " + suggested);
            Require(Field<TextBox>(form, "title").Text == "К-р 3-1'", "the new tab is not open: " + Field<TextBox>(form, "title").Text);
            Require(Field<TextBox>(form, "subtitle").Text == "к-р 3 пк0+72 к-л К-2", "receiver line: " + Field<TextBox>(form, "subtitle").Text);
            var outlet = Row(grid, 0);
            // 145.60 + (146.27 − 145.60) · 34 / 112 = 145.8034 at 72 m, written to the millimetre.
            Require(Equals(outlet.Cells["ProjectBottom"].Value, 145.803) && Equals(outlet.Cells["Ground"].Value, 147.25),
                "outlet: " + outlet.Cells["ProjectBottom"].Value + " / " + outlet.Cells["Ground"].Value);
            Require(Call(form, "OutletLock", outlet.Index, grid.Columns["ProjectBottom"].Index) is string, "outlet drain elevation is editable");
            Require(Call(form, "OutletLock", outlet.Index, grid.Columns["PeatDepth"].Index) == null, "peat of the outlet is locked");
            var saved = (ProfileProject)typeof(ProfilesForm).GetProperty("Project").GetValue(form);
            Require(saved.Template == ProfileTemplate.Drainage && saved.Settings.HorizontalScale == 2000 && saved.Settings.VerticalScale == 100,
                "the tab did not take the collector type and scales");
            Require(saved.AdditionalProfiles.Single(profile => profile.Name == "К-р 3'").Longitudinal.Single(point => point.Station == 72).PointName == "3-1",
                "the collector row did not get the label");
            string status = Field<Label>(form, "status").Text;
            Require(status == "Создан «К-р 3-1'»: впадает в «К-р 3'» на ПК 0+72. Устье взято с «К-р 3'»: поверхность 147.25, дрена 145.80. Введите ординаты коллектора.",
                "status: " + status);
            // A row added later is free; only the outlet follows the collector.
            grid.Rows.Add();
            var added = grid.Rows[grid.Rows.Count - 2];
            added.Cells["Station"].Value = 40.0;
            added.Cells["Ground"].Value = 147.6;
            added.Cells["ProjectBottom"].Value = 146.05;
            Require(Call(form, "OutletLock", added.Index, grid.Columns["ProjectBottom"].Index) == null, "an ordinate after the outlet is locked");
            Require((bool)Call(form, "TrySaveCurrent", false, false), "the tributary was not saved");
            saved = (ProfileProject)typeof(ProfilesForm).GetProperty("Project").GetValue(form);
            var texts = ProfileEngine.Build(saved).Entities.OfType<DrawText>().ToList();
            Require(texts.Any(text => text.Text == "3" && text.RotationDegrees == 90), "the receiving collector is not shown at ordinate 0");
            Require(texts.Any(text => text.Text == "к-р 3 пк0+72") && texts.Any(text => text.Text == "к-л К-2"), "the receiver line is not drawn");
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);

    private static void Prepare(ProfilesForm form)
    {
        // The tests have no DWG host; suppress only close-time export prompts in teardown.
        typeof(ProfilesForm).GetField("accepted", Private).SetValue(form, true);
        // Mono has incomplete ListBox painting. Exercise the full form controls and
        // handlers without painting it; the grid/menu tests still show real windows.
        if (Type.GetType("Mono.Runtime") == null) { form.Show(); Application.DoEvents(); }
    }

    private static void NameAssigns(string name)
    {
        var initial = Project();
        using (var form = new ProfilesForm(ProfileKind.CrossSection, initial))
        {
            Prepare(form);
            var input = Field<TextBox>(form, "sectionName");
            input.Text = name;
            typeof(Control).GetMethod("OnKeyDown", Private).Invoke(input, new object[] { new KeyEventArgs(Keys.Enter) });
            Application.DoEvents();
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(result.Sections[0].Station == 20, "name did not assign station");
            Require(result.Longitudinal.Single(p => p.Station == 20).ExistingBottom == 109.25, "bottom was not transferred");
            Require(result.Longitudinal.Single(p => p.Station == 20).LeftBank == 111.5, "bank was not transferred");
            Require(result.Sections[0].Points[2].DistanceFromPrevious == 3, "assignment lost measured distance");
            Require(!input.ReadOnly, "station name cannot be edited after assignment");
            var grid = Field<DataGridView>(form, "longGrid");
            Require(grid.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && Equals(r.Cells["ExistingBottom"].Value, 109.25)), "longitudinal UI is stale");
            Field<TabControl>(form, "tabs").SelectedTab = Field<TabPage>(form, "longPage");
            Application.DoEvents();
        }
    }

    private static void DeleteProfile()
    {
        var first = Project();
        var source = ProfileCollection.Add(first, "Лишний");
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, source))
        {
            Prepare(form);
            Require(Field<Button>(form, "deleteProfileButton").Enabled, "delete is not available for extra profile");
            Call(form, "RemoveCurrentProfile");
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(result.ProfileId == first.ProfileId, "surviving profile is not selected");
            Require(result.AdditionalProfiles.Count == 0, "deleted profile was retained");
            Require(result.Longitudinal.Count == first.Longitudinal.Count, "surviving data lost");
            Require(result.Sections.Count == first.Sections.Count, "surviving sections lost");
            Require(Field<TabControl>(form, "profileTabs").TabCount == 1, "deleted tab is still displayed");
            Require(!Field<Button>(form, "deleteProfileButton").Enabled, "single remaining profile can be deleted");
        }
    }

    private static void EndStation()
    {
        var source = Project();
        source.Longitudinal = new List<LongitudinalPoint>
        {
            new LongitudinalPoint { Station = 0, ExistingBottom = 110, ProjectBottom = 108 },
            new LongitudinalPoint { Station = 20, ExistingBottom = 108, ProjectBottom = 107 },
            new LongitudinalPoint { Station = 50 }
        };
        source.Sections.Clear();
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, source))
        {
            Prepare(form);
            Call(form, "RecalculateDerivedValues");
            var derived = Field<Dictionary<string, double>>(form, "derivedValues");
            Require(derived["2:ExistingBottom"] == 105, "existing bottom did not extrapolate");
            Require(derived["2:ProjectBottom"] == 105.5, "design bottom did not extrapolate");
            var grid = Field<DataGridView>(form, "longGrid");
            grid.Rows[1].Cells["ProjectBottom"].Value = 106.0;
            Call(form, "RecalculateDerivedValues");
            Require(derived["2:ProjectBottom"] == 103, "changed anchor did not recalculate endpoint");
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(!result.Longitudinal[2].ProjectBottom.HasValue, "derived endpoint became a fixed anchor");
        }
    }

    private static void FillRow()
    {
        var source = Project();
        source.Sections.Clear();
        source.Longitudinal = new List<LongitudinalPoint>
        {
            new LongitudinalPoint { Station = 0, LeftBank = 110, RightBank = 111, ExistingBottom = 100,
                ProjectBottom = 99, Water = 105, BottomWidth = 4, LeftSlope = 1, RightSlope = 2 },
            new LongitudinalPoint { Station = 20, LeftBank = 108, RightBank = 109, ExistingBottom = 98,
                ProjectBottom = 97, Water = 104, BottomWidth = 6, LeftSlope = 2, RightSlope = 3 },
            new LongitudinalPoint { Station = 50, RightBank = 120 }
        };
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, source))
        {
            Prepare(form);
            var grid = Field<DataGridView>(form, "longGrid");
            Require(grid.Columns["FillMissing"].Visible && grid.Columns["FillMissing"].Frozen, "fill button is not visible beside station");
            var before = (ProfileProject)Call(form, "ReadProject");
            Require(!before.Longitudinal[2].LeftBank.HasValue && !before.Longitudinal[2].BottomWidth.HasValue,
                "empty cells were filled without a click");
            var target = grid.Rows.Cast<DataGridViewRow>().Single(row => !row.IsNewRow && Equals(row.Cells["Station"].Value, 50.0));
            typeof(DataGridView).GetMethod("OnCellContentClick", Private).Invoke(grid,
                new object[] { new DataGridViewCellEventArgs(grid.Columns["FillMissing"].Index, target.Index) });
            var result = (ProfileProject)Call(form, "ReadProject");
            var filled = result.Longitudinal.Single(point => point.Station == 50);
            Require(filled.LeftBank == 105 && filled.Water == 102.5 && filled.BottomWidth == 9, "row button lost bank/water/width continuation");
            Require(filled.LeftSlope == 3.5 && filled.RightSlope == 4.5, "slopes were not filled");
            Require(filled.RightBank == 120, "manual bank was overwritten");
            Call(form, "SelectTemplate", ProfileTemplate.Nvk);
            Require(grid.Columns["FillMissing"].Visible, "template switch hid row button");
            Require(!(bool)typeof(ProfilesForm).GetMethod("RowEmpty", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { target }), "filled row became empty");
            int blank = grid.Rows.Add();
            Require((bool)typeof(ProfilesForm).GetMethod("RowEmpty", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { grid.Rows[blank] }), "button turned blank row into data");
        }
    }

    private static void OrdinaryName()
    {
        using (var form = new ProfilesForm(ProfileKind.CrossSection, Project()))
        {
            Prepare(form);
            Field<TextBox>(form, "sectionName").Text = "Промер у моста";
            Call(form, "CommitSectionName");
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(!result.Sections[0].Station.HasValue, "ordinary name was treated as station");
            Require(result.Sections[0].Name == "Промер у моста", "ordinary name was lost");
        }
    }

    private static void StationPicker()
    {
        var source = Project();
        source.Longitudinal[0].Station = 20.123456789;
        using (var form = new ProfilesForm(ProfileKind.CrossSection, source))
        using (var timer = new Timer { Interval = 100 })
        {
            Prepare(form);
            bool clicked = false;
            timer.Tick += (s, e) =>
            {
                var dialog = Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f => f.Text == "Назначить на продольный профиль");
                if (dialog == null) return;
                var controls = Descendants(dialog).ToList();
                var picker = controls.OfType<ComboBox>().Single();
                Require(picker.Items.Count == 1, "station picker did not list existing longitudinal station");
                picker.SelectedIndex = 0;
                clicked = true;
                timer.Stop();
                controls.OfType<Button>().Single(button => button.Text == "Назначить").PerformClick();
            };
            timer.Start();
            Call(form, "ShowStationAssignment");
            Require(clicked, "station assignment button was not exercised");
            var result = (ProfileProject)Call(form, "ReadProject");
            Require(result.Sections[0].Station == 20.123456789, "station picker rounded its exact station");
            Require(result.Longitudinal.Single().ExistingBottom == 109.25, "picker did not transfer survey elevations");
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void InterpolatedDimensions()
    {
        var project = new ProfileProject
        {
            Longitudinal = new List<LongitudinalPoint>
            {
                new LongitudinalPoint { Station = 0, ProjectBottom = 10, BottomWidth = 2, LeftSlope = 1, RightSlope = 2 },
                new LongitudinalPoint { Station = 20 },
                new LongitudinalPoint { Station = 40, ProjectBottom = 12, BottomWidth = 4, LeftSlope = 3, RightSlope = 4 }
            }
        };
        using (var form = new ProfilesForm(ProfileKind.Longitudinal, project))
        {
            Prepare(form);
            Call(form, "RecalculateDerivedValues");
            var values = Field<Dictionary<string, double>>(form, "derivedValues");
            Require(values["1:ProjectBottom"] == 11, "missing project bottom interpolation");
            Require(values["1:BottomWidth"] == 3, "missing bottom width interpolation");
            Require(values["1:LeftSlope"] == 2 && values["1:RightSlope"] == 3, "missing slope interpolation");
            var raw = (ProfileProject)Call(form, "ReadProject");
            Require(!raw.Longitudinal[1].BottomWidth.HasValue, "UI promoted interpolation into raw anchor");
        }
    }

    private static void ApproximateCrossSection()
    {
        var source = new ProfileProject
        {
            Kind = ProfileKind.CrossSection,
            Longitudinal = new List<LongitudinalPoint>
            {
                new LongitudinalPoint { Station = 20, LeftBank = 110, ExistingBottom = 100, RightBank = 112 }
            }
        };
        source.Sections.Add(ProfileInterpolation.CreateSection(source.Longitudinal, 20));
        using (var form = new ProfilesForm(ProfileKind.CrossSection, source))
        {
            Prepare(form);
            var grid = Field<SectionPointGrid>(form, "crossGrid");
            // Mono cannot move CurrentCell in a hidden grid; host the production
            // control in a visible window while retaining its form event handlers.
            var originalParent = grid.Parent;
            using var gridHost = new Form { ClientSize = new System.Drawing.Size(1000, 220), ShowInTaskbar = false };
            gridHost.Controls.Add(grid); gridHost.Show(); Application.DoEvents();
            grid.CurrentCell = grid.Rows[1].Cells[1];
            grid.AddPointAfter(); grid.AddPointAfter();
            int bottomIndex = grid.ReadPoints().FindIndex(p => p.Role == SectionPointRole.Bottom);
            grid.CurrentCell = grid.Rows[1].Cells[bottomIndex + 1];
            grid.AddPointAfter(); grid.AddPointAfter();
            Require(grid.ReadPoints().Count == 7, "inserted columns were lost");
            Require(grid.ReadPoints().Count(p => !p.Existing.HasValue) == 4, "new elevations filled without clicking");
            Exception error = null;
            int stage = 0;
            using (var timer = new Timer { Interval = 60 })
            {
                timer.Tick += (sender, args) =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "Ширина между берегами");
                    if (dialog == null) return;
                    try
                    {
                        var controls = Descendants(dialog).ToList();
                        var width = controls.OfType<TextBox>().Single(t => t.AccessibleName == "Ширина между берегами, м");
                        var bottom = controls.OfType<TextBox>().Single(t => t.AccessibleName == "Дно от левого берега, м");
                        var apply = controls.OfType<Button>().Single(b => b.Text == "Распределить");
                        if (stage++ == 0)
                        {
                            width.Text = "-1"; apply.PerformClick();
                            Require(dialog.DialogResult != DialogResult.OK, "negative width was accepted");
                            return;
                        }
                        width.Text = "21,5"; bottom.Text = "8,5";
                        apply.PerformClick();
                        Require(dialog.DialogResult == DialogResult.OK, "valid comma-decimal width was rejected");
                        timer.Stop();
                    }
                    catch (Exception ex) { error = ex; timer.Stop(); dialog.DialogResult = DialogResult.Cancel; }
                };
                timer.Start();
                typeof(Button).GetMethod("OnClick", Private).Invoke(Field<Button>(form, "crossBankWidthButton"), new object[] { EventArgs.Empty });
            }
            if (error != null) throw error;
            var spaced = grid.ReadPoints();
            Require(Math.Abs(spaced.Skip(1).Sum(p => p.DistanceFromPrevious.Value) - 21.5) < 1e-9, "width was not distributed");
            Require(spaced.Count(p => !p.Existing.HasValue) == 4, "distance action changed elevations");
            // A hand-entered intermediate point must survive automatic filling.
            grid.SetElevationAt(1, false, 108.75);
            typeof(Button).GetMethod("OnClick", Private).Invoke(Field<Button>(form, "fillCrossElevationsButton"), new object[] { EventArgs.Empty });
            var result = (ProfileProject)Call(form, "ReadProject");
            var section = result.Sections.Single();
            Require(section.Points.All(p => p.Existing.HasValue), "blank elevations remain");
            Require(section.Points[1].Existing == 108.75, "manual intermediate elevation changed");
            Require(section.Points[3].Existing == 100 && section.Points[3].UseLongitudinalElevation, "linked bottom was lost");
            Require(section.Points[2].Existing > 100 && section.Points[4].Existing > 100, "points beside bottom are not higher");
            Require(section.ProjectAnchorIndex == 3, "insertion lost project anchor index");
            var saved = ProjectFile.Deserialize(ProjectFile.Serialize(result));
            Require(saved.Sections[0].Points.All(p => p.Existing.HasValue), "saved estimates were lost");
            Require(saved.Longitudinal.Single().LeftBank == 110, "filling changed longitudinal anchors");
            Require(ProfileEngine.Build(saved).Entities.Count > 0, "filled section did not build");
            gridHost.Controls.Remove(grid); originalParent.Controls.Add(grid);
        }
    }

    private static ProfileProject Project() => new ProfileProject
    {
        Longitudinal = new List<LongitudinalPoint> { new LongitudinalPoint { Station = 20 } },
        Sections = new List<CrossSection> { DrawingSurvey.BuildSection(new List<SectionSurveyPick>
        {
            new SectionSurveyPick { Position = new Point2(0, 0), Values = new SectionPoint { Existing = 111.5 } },
            new SectionSurveyPick { Position = new Point2(2, 0), Values = new SectionPoint { Existing = 109.25 } },
            new SectionSurveyPick { Position = new Point2(5, 0), Values = new SectionPoint { Existing = 112.5 } }
        }, SurveySourceUnits.ModelMetres(), null, "Промер 1") }
    };

    private static void Check(string name, Action action)
    {
        action(); Passed++; Console.WriteLine("PASS: " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
