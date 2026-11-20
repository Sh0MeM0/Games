using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Profiles.AutoCAD;
using Profiles.Core;

/// <summary>
/// Version 0.9.0: work is never lost when the profile type, the project or the
/// drawing changes. The real editor and its handlers run with an in-memory
/// "DWG" and a temporary autosave library; AutoCAD is not involved.
/// </summary>
internal static class AutosaveWorkflowTests
{
    internal static int Passed;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        Check("switching work type back returns to the profile with its data instead of a new empty tab", SwitchBackKeepsData);
        Check("an empty profile changes type in place without extra tabs", EmptyProfileChangesType);
        Check("changing the type of a profile with data keeps every value", ChangeTypeKeepsData);
        Check("a new empty tab of another type keeps the original tab untouched", NewTabKeepsOriginal);
        Check("every accepted change reaches the DWG and the autosave library", ChangesReachBothStores);
        Check("another drawing offers the autosaved project and copies it only after a change", OtherDrawingContinuesWork);
        Check("an unchanged project is not rewritten into the DWG", UnchangedNotRewritten);
        Check("a failing DWG write does not block switching and nothing is lost", DrawingFailureDoesNotBlock);
        Check("earlier versions and deleted projects are restored", VersionsAndDeleted);
        Check("exported JSON stays linked and autosaved in the next editor session", JsonLinkPersists);
    }

    private sealed class FakeDrawing
    {
        internal readonly Dictionary<string, string> Records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal int Writes;
        internal bool Fail;
        internal readonly string Key = Guid.NewGuid().ToString("N");
        internal void Save(ProfileProject project)
        {
            if (Fail) throw new InvalidOperationException("чертёж открыт только для чтения");
            Writes++;
            Records[project.ProjectId] = ProjectFile.Serialize(project);
        }
        internal void Delete(string id) => Records.Remove(id);
        internal List<ProfileProject> Projects => Records.Values.Select(ProjectFile.Deserialize).ToList();
    }

    private sealed class Session : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "profiles-ui-autosave-" + Guid.NewGuid().ToString("N"));
        internal ProjectLibrary Library => new ProjectLibrary(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }

        internal ProfilesForm Open(FakeDrawing drawing, string drawingName, ProfileKind kind = ProfileKind.Longitudinal)
        {
            var library = Library;
            var sync = library.Synchronize(drawing.Projects, drawing.Records.Count > 0 ? drawing.Key : null, drawingName);
            var autosave = new ProfilesAutosave(library, sync, drawingName, () => drawing.Records.Count > 0 ? drawing.Key : null);
            var initial = sync.DrawingProjects.FirstOrDefault() ??
                (sync.LibraryOnly.Count > 0 ? library.Load(sync.LibraryOnly[0].ProjectId) : null);
            var form = new ProfilesForm(kind, initial, sync.DrawingProjects, drawing.Save, null, drawing.Delete, autosave);
            Prepare(form);
            return form;
        }
    }

    private static T Field<T>(object target, string name)
    {
        var field = typeof(ProfilesForm).GetField(name, Private);
        if (field != null) return (T)field.GetValue(target);
        return (T)typeof(ProfilesForm).GetProperty(name, Private | BindingFlags.Public).GetValue(target);
    }

    private static object Call(object target, string name, params object[] args)
    {
        var method = typeof(ProfilesForm).GetMethods(Private).First(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(target, args);
    }

    private static void Prepare(ProfilesForm form)
    {
        typeof(ProfilesForm).GetField("accepted", Private).SetValue(form, true);
        if (Type.GetType("Mono.Runtime") == null) { form.Show(); Application.DoEvents(); }
    }

    private static ProfileProject ReadProject(ProfilesForm form) => (ProfileProject)Call(form, "ReadProject");

    private static bool Save(ProfilesForm form) => (bool)Call(form, "TrySaveCurrent", true, false);

    private static void Choose(ProfilesForm form, string choice)
    {
        var type = typeof(ProfilesForm).GetNestedType("TemplateSwitchChoice", BindingFlags.NonPublic);
        var value = Enum.Parse(type, choice);
        var delegateType = typeof(Func<,,,>).MakeGenericType(typeof(string), typeof(string), typeof(string), type);
        var method = typeof(AutosaveWorkflowTests).GetMethod(nameof(Constant), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(type);
        var chooser = method.Invoke(null, new[] { value });
        typeof(ProfilesForm).GetProperty("ChooseTemplateSwitch", Private).SetValue(form, Delegate.CreateDelegate(delegateType, chooser, "Invoke"));
    }

    private static Func<string, string, string, T> Constant<T>(T value) => (requested, current, existing) => value;

    private static void SwitchTemplate(ProfilesForm form, ProfileTemplate template)
    {
        var picker = Field<ComboBox>(form, "templatePicker");
        for (int i = 0; i < picker.Items.Count; i++)
            if (picker.Items[i].ToString() == ProfilesFormTemplateName(template)) picker.SelectedIndex = i;
        Call(form, "TemplateSelectionChanged", picker, EventArgs.Empty);
    }

    private static string ProfilesFormTemplateName(ProfileTemplate template) =>
        template == ProfileTemplate.NewChannel ? "Новый канал" : template == ProfileTemplate.Nvk ? "Сети НВК" : "Подчистка канала";

    private static void EnterStations(ProfilesForm form, params double[] bottoms)
    {
        var grid = Field<DataGridView>(form, "longGrid");
        for (int i = 0; i < bottoms.Length; i++)
        {
            int row = grid.Rows.Add();
            grid.Rows[row].Cells["Station"].Value = (i * 20.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            grid.Rows[row].Cells["ExistingBottom"].Value = bottoms[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            grid.Rows[row].Cells["LeftBank"].Value = (bottoms[i] + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Call(form, "SetDirty");
    }

    private static List<ProfileSummary> Tabs(ProfilesForm form) => ProfileCollection.Describe(ReadProject(form));

    private static void SwitchBackKeepsData()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Канал 7");
                EnterStations(form, 101, 100.5, 100);
                Require(Save(form), "data was not saved");
                Choose(form, "CreateNew");
                SwitchTemplate(form, ProfileTemplate.NewChannel);
                Require(ReadProject(form).Template == ProfileTemplate.NewChannel && ReadProject(form).Longitudinal.Count == 0, "new channel tab not opened");
                EnterStations(form, 90);
                Require(Save(form), "new channel data was not saved");
                Choose(form, "OpenExisting");
                SwitchTemplate(form, ProfileTemplate.Reconstruction);
                var back = ReadProject(form);
                Require(back.Template == ProfileTemplate.Reconstruction, "type did not switch back");
                Require(back.Longitudinal.Count == 3 && back.Longitudinal[1].ExistingBottom == 100.5, "original survey lost on switching back");
                Require(Tabs(form).Count == 2, "switching back created another empty tab: " + string.Join(", ", Tabs(form).Select(t => t.Name)));
                SwitchTemplate(form, ProfileTemplate.NewChannel);
                Require(ReadProject(form).Longitudinal.Single().ExistingBottom == 90, "new channel data lost");
                Require(Tabs(form).Count == 2, "a third tab appeared");
                var stored = ProjectFile.Deserialize(drawing.Records.Values.Single());
                Require(ProfileCollection.Describe(stored).Count(t => t.HasData) == 2, "DWG does not hold both profiles");
            }
        }
    }

    private static void EmptyProfileChangesType()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Пустой");
                SwitchTemplate(form, ProfileTemplate.Nvk);
                SwitchTemplate(form, ProfileTemplate.NewChannel);
                var tabs = Tabs(form);
                Require(tabs.Count == 1, "empty profile produced extra tabs");
                Require(tabs[0].Template == ProfileTemplate.NewChannel, "type was not changed in place");
                Require(Field<DataGridView>(form, "longGrid").Columns["PeatDepth"].Visible, "new channel columns not shown");
                var caption = Field<TabControl>(form, "profileTabs").TabPages[0].Text;
                Require(caption.Contains("новый канал"), "tab caption does not show the type: " + caption);
            }
        }
    }

    private static void ChangeTypeKeepsData()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Ошибся типом");
                EnterStations(form, 101, 100);
                Require(Save(form), "data not saved");
                Choose(form, "ChangeCurrent");
                SwitchTemplate(form, ProfileTemplate.NewChannel);
                var changed = ReadProject(form);
                Require(Tabs(form).Count == 1, "changing type created a tab");
                Require(changed.Template == ProfileTemplate.NewChannel, "type not changed");
                Require(changed.Longitudinal.Count == 2 && changed.Longitudinal[0].LeftBank == 103, "hidden values were dropped");
                SwitchTemplate(form, ProfileTemplate.Reconstruction);
                var back = ReadProject(form);
                Require(back.Longitudinal[1].ExistingBottom == 100 && back.Longitudinal[1].LeftBank == 102, "values lost after switching back");
                Choose(form, "Cancel");
                SwitchTemplate(form, ProfileTemplate.Nvk);
                Require(ReadProject(form).Template == ProfileTemplate.Reconstruction, "cancel changed the type");
                Require(Field<ComboBox>(form, "templatePicker").SelectedItem.ToString() == "Подчистка канала", "picker not restored after cancel");
            }
        }
    }

    private static void NewTabKeepsOriginal()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Два типа");
                EnterStations(form, 101, 100);
                Choose(form, "CreateNew");
                SwitchTemplate(form, ProfileTemplate.Nvk);
                var tabs = Tabs(form);
                Require(tabs.Count == 2 && tabs[1].Template == ProfileTemplate.Nvk && !tabs[1].HasData, "no empty NVK tab");
                Require(tabs[0].HasData && tabs[0].Template == ProfileTemplate.Reconstruction, "original tab changed");
            }
        }
    }

    private static void ChangesReachBothStores()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Автосохранение");
                EnterStations(form, 101, 100);
                Require(Save(form), "not saved");
                string id = ReadProject(form).ProjectId;
                Require(ProjectFile.Deserialize(drawing.Records[id]).Longitudinal.Count == 2, "DWG not updated");
                Require(session.Library.Load(id).Longitudinal.Count == 2, "library not updated");
                Require(!(bool)typeof(ProfilesForm).GetField("dirty", Private).GetValue(form), "form still marked unsaved");
            }
        }
    }

    private static void OtherDrawingContinuesWork()
    {
        using (var session = new Session())
        {
            var first = new FakeDrawing();
            string id;
            using (var form = session.Open(first, "Лист 1.dwg"))
            {
                Call(form, "NewProjectNamed", "Канал через листы");
                EnterStations(form, 101, 100, 99.5);
                Require(Save(form), "not saved");
                id = ReadProject(form).ProjectId;
                Call(form, "ClosingForm", form, new FormClosingEventArgs(CloseReason.UserClosing, false));
            }
            var second = new FakeDrawing();
            using (var form = session.Open(second, "Лист 2.dwg"))
            {
                var opened = ReadProject(form);
                Require(opened.ProjectId == id && opened.Longitudinal.Count == 3, "other drawing does not continue the autosaved project");
                var picker = Field<ComboBox>(form, "projectPicker");
                Require(picker.Items.Cast<object>().Any(item => item.ToString().Contains("автосохранение") && item.ToString().Contains("Лист 1.dwg")), "picker does not show the source drawing");
                // Browsing and switching away do not copy it into this DWG.
                Call(form, "NewProjectNamed", "Свой проект");
                Require(!second.Records.ContainsKey(id), "browsed project was copied into the second DWG");
                picker.SelectedIndex = picker.Items.Cast<object>().ToList().FindIndex(item => item.ToString().StartsWith("Канал через листы"));
                Call(form, "ProjectSelectionChanged", picker, EventArgs.Empty);
                Require(ReadProject(form).ProjectId == id, "autosaved project not opened from the picker");
                var grid = Field<DataGridView>(form, "longGrid");
                grid.Rows[0].Cells["ExistingBottom"].Value = "101.25";
                Call(form, "SetDirty");
                Require(Save(form), "edit not saved");
                Require(second.Records.ContainsKey(id), "edited project was not written into the second DWG");
                Require(session.Library.Load(id).Longitudinal[0].ExistingBottom == 101.25, "library missed the edit");
            }
            // The first drawing still holds its earlier state: the newer work wins.
            using (var form = session.Open(first, "Лист 1.dwg"))
                Require(ReadProject(form).Longitudinal[0].ExistingBottom == 101.25, "first drawing lost the work made in the second");
        }
    }

    private static void UnchangedNotRewritten()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Без изменений");
                EnterStations(form, 101, 100);
                Require(Save(form), "not saved");
                int writes = drawing.Writes;
                for (int i = 0; i < 5; i++) Require(Save(form), "repeat save failed");
                Require(drawing.Writes == writes, "unchanged data was rewritten " + (drawing.Writes - writes) + " times");
            }
        }
    }

    private static void DrawingFailureDoesNotBlock()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Только чтение");
                EnterStations(form, 101, 100);
                Require(Save(form), "not saved");
                string id = ReadProject(form).ProjectId;
                drawing.Fail = true;
                var grid = Field<DataGridView>(form, "longGrid");
                grid.Rows[1].Cells["ExistingBottom"].Value = "98.5";
                Call(form, "SetDirty");
                Require(Save(form), "a read-only DWG blocked saving although the autosave works");
                Require(session.Library.Load(id).Longitudinal[1].ExistingBottom == 98.5, "edit lost while the DWG was read-only");
                Require(Field<Label>(form, "status").Text.Contains("Не записано в DWG"), "user not told about the DWG");
                drawing.Fail = false;
                Require(Save(form), "retry failed");
                Require(ProjectFile.Deserialize(drawing.Records[id]).Longitudinal[1].ExistingBottom == 98.5, "DWG write was not retried");
            }
        }
    }

    private static void VersionsAndDeleted()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "Версии");
                EnterStations(form, 101, 100);
                Require(Save(form), "not saved");
                string id = ReadProject(form).ProjectId;
                session.Library.Checkpoint(id);
                var grid = Field<DataGridView>(form, "longGrid");
                grid.Rows[0].Cells["ExistingBottom"].Value = "50";
                Call(form, "SetDirty");
                Require(Save(form), "edit not saved");
                var earlier = session.Library.Versions(id).First(version => !version.IsCurrent);
                Call(form, "RestoreVersion", earlier.VersionId, earlier.SavedUtc);
                Require(ReadProject(form).Longitudinal[0].ExistingBottom == 101, "earlier version not restored");
                Require(session.Library.Versions(id).Skip(1).Any(version =>
                    session.Library.LoadVersion(id, version.VersionId).Longitudinal[0].ExistingBottom == 50), "state before restoring was not kept");
                var other = typeof(ProfilesForm).GetField("deleteProjectFromDrawing", Private);
                Require(other != null, "delete delegate missing");
                // Delete without the confirmation dialog: the real handler body after confirmation.
                Call(form, "DeleteProjectConfirmed");
                Require(!drawing.Records.ContainsKey(id), "project left in DWG");
                var deleted = session.Library.ListDeleted().Single(entry => entry.ProjectId == id);
                Call(form, "RestoreDeletedProject", deleted.DeletedId);
                Require(ReadProject(form).ProjectId == id && ReadProject(form).Longitudinal.Count == 2, "deleted project not restored");
                Require(drawing.Records.ContainsKey(id), "restored project not written to DWG");
            }
        }
    }

    private static void JsonLinkPersists()
    {
        using (var session = new Session())
        {
            var drawing = new FakeDrawing();
            string file = Path.Combine(session.Root, "Экспорт.json");
            Directory.CreateDirectory(session.Root);
            string id;
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Call(form, "NewProjectNamed", "JSON");
                EnterStations(form, 101, 100);
                Require(Save(form), "not saved");
                id = ReadProject(form).ProjectId;
                Call(form, "ExportTo", file);
                Require(File.Exists(file), "JSON not exported");
                Require(Field<CheckBox>(form, "autoSaveJson").Checked, "export did not enable JSON autosave");
            }
            using (var form = session.Open(drawing, "План.dwg"))
            {
                Require(Field<CheckBox>(form, "autoSaveJson").Checked, "JSON autosave forgotten in the next session");
                var grid = Field<DataGridView>(form, "longGrid");
                grid.Rows[0].Cells["ExistingBottom"].Value = "77";
                Call(form, "SetDirty");
                Require(Save(form), "edit not saved");
                Require(ProjectFile.Load(file).Longitudinal[0].ExistingBottom == 77, "linked JSON not updated in the next session");
            }
        }
    }

    private static void Check(string name, Action action)
    {
        action(); Passed++; Console.WriteLine("PASS: " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
