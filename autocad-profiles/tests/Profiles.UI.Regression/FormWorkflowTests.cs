using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Profiles.AutoCAD;
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
        Check("station picker assigns existing station without rounding its precision", StationPicker);
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
