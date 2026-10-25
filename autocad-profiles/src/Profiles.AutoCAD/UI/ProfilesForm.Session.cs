using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>Transient editor state carried across command-line capture prompts.</summary>
    internal sealed class ProfileEditorSession
    {
        internal string ProjectId;
        internal string ProfileId;
        internal string SelectedSectionId;
        internal ProfileKind Kind;
        internal string Tab;
        internal bool AllSections;
        internal bool AllProfiles;
        internal bool ShowEarthworks;
        internal Rectangle Bounds;
        internal FormWindowState WindowState;
        internal int Point;
        internal int PointRow;
        internal double? LongitudinalStation;
        internal string LongitudinalColumn;
        internal readonly List<ProfileProject> Projects = new List<ProfileProject>();
        internal readonly Dictionary<string, string> JsonPaths = new Dictionary<string, string>();
        internal readonly HashSet<string> JsonAutoSaveProjects = new HashSet<string>();
        internal readonly Dictionary<string, string> LastWrittenJson = new Dictionary<string, string>();
        internal readonly HashSet<string> MemoryOnlyProjects = new HashSet<string>();
        internal readonly Dictionary<string, string> ProfileSectionSelections = new Dictionary<string, string>();
    }

    public sealed partial class ProfilesForm
    {
        internal ProfileEditorSession CaptureEditorSession()
        {
            var state = new ProfileEditorSession
            {
                ProjectId = currentProjectId, ProfileId = CurrentProfileId, SelectedSectionId = SelectedSectionId, Kind = activeKind,
                Tab = tabs.SelectedTab == previewPage ? "preview" : tabs.SelectedTab == formatPage ? "format" :
                    tabs.SelectedTab == crossPage ? "cross" : "long",
                AllSections = AllSections, AllProfiles = AllProfiles, Point = crossGrid.SelectedPointIndex, PointRow = (int)crossGrid.SelectedRowKind,
                ShowEarthworks = showEarthworks.Checked, Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds,
                WindowState = WindowState,
                LongitudinalColumn = longGrid.CurrentCell?.OwningColumn.Name
            };
            if (longGrid.CurrentCell != null && !longGrid.CurrentCell.OwningRow.IsNewRow)
            {
                try { state.LongitudinalStation = Required(longGrid.CurrentCell.OwningRow, "Station"); }
                catch (ProfileValidationException) { }
            }
            state.Projects.AddRange(projects.Select(ProjectFile.Clone));
            foreach (var entry in jsonPaths) state.JsonPaths[entry.Key] = entry.Value;
            foreach (string id in jsonAutoSaveProjects) state.JsonAutoSaveProjects.Add(id);
            foreach (var entry in lastWrittenJson) state.LastWrittenJson[entry.Key] = entry.Value;
            foreach (string id in memoryOnlyProjects) state.MemoryOnlyProjects.Add(id);
            foreach (var entry in profileSectionSelections) state.ProfileSectionSelections[entry.Key] = entry.Value;
            return state;
        }

        internal void RestoreEditorSession(ProfileEditorSession state, string selectedSectionId = null)
        {
            if (state == null) return;
            bool old = loading;
            loading = true;
            try
            {
                // The session list replaces the catalog read when the command
                // started: a project deleted in the editor must not come back.
                var current = Project;
                projects.Clear();
                foreach (var item in state.Projects)
                    RegisterProject(current != null && string.Equals(item.ProjectId, current.ProjectId, StringComparison.OrdinalIgnoreCase) ? current : item);
                if (current != null) RegisterProject(current);
                jsonPaths.Clear();
                foreach (var entry in state.JsonPaths) jsonPaths[entry.Key] = entry.Value;
                jsonAutoSaveProjects.Clear();
                foreach (string id in state.JsonAutoSaveProjects) jsonAutoSaveProjects.Add(id);
                lastWrittenJson.Clear();
                foreach (var entry in state.LastWrittenJson) lastWrittenJson[entry.Key] = entry.Value;
                memoryOnlyProjects.Clear();
                foreach (string id in state.MemoryOnlyProjects) memoryOnlyProjects.Add(id);
                profileSectionSelections.Clear();
                foreach (var entry in state.ProfileSectionSelections) profileSectionSelections[entry.Key] = entry.Value;
                allCrossSections = state.AllSections;
                allLongitudinalProfiles = state.AllProfiles;
                showEarthworks.Checked = state.ShowEarthworks;
                if (state.Bounds.Width > 0 && state.Bounds.Height > 0)
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = state.Bounds;
                    WindowState = state.WindowState == FormWindowState.Minimized ? FormWindowState.Normal : state.WindowState;
                }
                RefreshProjectPicker();
                RestoreJsonSelection();
                if (string.Equals(currentProjectId, state.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(CurrentProfileId, state.ProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    RestoreSectionSelection(selectedSectionId ?? state.SelectedSectionId);
                    if (state.Point >= 0 && state.Point + 1 < crossGrid.Columns.Count &&
                        state.PointRow >= 0 && state.PointRow < crossGrid.Rows.Count)
                        crossGrid.CurrentCell = crossGrid.Rows[state.PointRow].Cells[state.Point + 1];
                    if (state.LongitudinalStation.HasValue && state.LongitudinalColumn != null &&
                        longGrid.Columns.Contains(state.LongitudinalColumn) && longGrid.Columns[state.LongitudinalColumn].Visible)
                    {
                        foreach (DataGridViewRow row in longGrid.Rows)
                            if (!row.IsNewRow && row.Cells["Station"].Value is double station && station == state.LongitudinalStation.Value)
                            { longGrid.CurrentCell = row.Cells[state.LongitudinalColumn]; break; }
                    }
                    tabs.SelectedTab = state.Tab == "preview" ? previewPage : state.Tab == "format" ? formatPage :
                        activeKind == ProfileKind.CrossSection ? crossPage : longPage;
                }
                UpdateModeCaption();
            }
            finally { loading = old; }
        }
    }
}
