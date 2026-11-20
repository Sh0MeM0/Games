using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private static string SizeText(long bytes) =>
            bytes < 1024 ? bytes + " Б" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " КБ" :
            (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " МБ";

        private void ShowVersions()
        {
            if (Library == null)
            {
                ShowError("Автосохранение недоступно в этом окне.");
                return;
            }
            if (!TrySaveCurrent(true)) return;
            using (var dialog = new Form
            {
                Text = "Версии проекта «" + currentProjectName + "»", Font = Font, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, ShowInTaskbar = false, Size = new Size(Px(760), Px(620)), MinimumSize = new Size(Px(560), Px(420))
            })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(Px(10)) };
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(Px(720), 0), Margin = new Padding(0, 0, 0, Px(6)),
                    Text = "Автосохранение хранит текущее состояние, прежние состояния (одно не реже чем раз в 3 минуты работы, а также перед удалением, " +
                           "заменой при импорте и восстановлением) и удалённые проекты. Восстановление само сохраняет текущее состояние в этот список."
                }, 0, 0);
                var versions = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
                versions.Columns.Add("Сохранено", Px(260));
                versions.Columns.Add("Размер", Px(110));
                foreach (var version in Library.Versions(currentProjectId))
                    versions.Items.Add(new ListViewItem(new[]
                    {
                        (version.IsCurrent ? "Текущая — " : "") + ProfilesAutosave.LocalTime(version.SavedUtc, seconds: true),
                        SizeText(version.Bytes)
                    }) { Tag = version });
                layout.Controls.Add(versions, 0, 1);
                var versionButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
                var openVersion = new Button { Text = "Открыть выбранную версию", AutoSize = true, Enabled = false };
                versionButtons.Controls.Add(openVersion);
                layout.Controls.Add(versionButtons, 0, 2);
                layout.Controls.Add(new Label { Text = "Удалённые проекты", AutoSize = true, Font = BoldFont, Margin = new Padding(0, Px(10), 0, Px(4)) }, 0, 3);
                var deleted = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
                deleted.Columns.Add("Проект", Px(280));
                deleted.Columns.Add("Удалён", Px(150));
                deleted.Columns.Add("Чертёж", Px(220));
                foreach (var entry in Library.ListDeleted())
                    deleted.Items.Add(new ListViewItem(new[]
                    {
                        entry.Name, entry.DeletedUtc.HasValue ? ProfilesAutosave.LocalTime(entry.DeletedUtc.Value) : "", entry.SourceDrawing ?? ""
                    }) { Tag = entry });
                layout.Controls.Add(deleted, 0, 4);
                var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
                var restoreDeleted = new Button { Text = "Восстановить проект", AutoSize = true, Enabled = false };
                var folder = new Button { Text = "Открыть папку автосохранения", AutoSize = true };
                bottom.Controls.Add(restoreDeleted);
                bottom.Controls.Add(folder);
                layout.Controls.Add(bottom, 0, 5);
                var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
                layout.Controls.Add(close, 0, 6);
                dialog.Controls.Add(layout);
                dialog.CancelButton = close;
                tips.SetToolTip(folder, Library.RootPath);
                versions.SelectedIndexChanged += (s, e) =>
                    openVersion.Enabled = versions.SelectedItems.Count == 1 && !((LibraryVersion)versions.SelectedItems[0].Tag).IsCurrent;
                deleted.SelectedIndexChanged += (s, e) => restoreDeleted.Enabled = deleted.SelectedItems.Count == 1;
                openVersion.Click += (s, e) =>
                {
                    if (versions.SelectedItems.Count != 1) return;
                    var version = (LibraryVersion)versions.SelectedItems[0].Tag;
                    dialog.DialogResult = DialogResult.OK;
                    RestoreVersion(version.VersionId, version.SavedUtc);
                };
                restoreDeleted.Click += (s, e) =>
                {
                    if (deleted.SelectedItems.Count != 1) return;
                    var entry = (LibraryEntry)deleted.SelectedItems[0].Tag;
                    dialog.DialogResult = DialogResult.OK;
                    RestoreDeletedProject(entry.DeletedId);
                };
                folder.Click += (s, e) =>
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(Library.RootPath);
                        Process.Start(new ProcessStartInfo { FileName = Library.RootPath, UseShellExecute = true });
                    }
                    catch (Exception error) { ShowError("Не удалось открыть папку " + Library.RootPath + ": " + error.Message); }
                };
                dialog.ShowDialog(this);
            }
        }

        /// <summary>Opens an earlier autosaved state of the current project; the present state is kept in the history first.</summary>
        internal void RestoreVersion(string versionId, DateTime savedUtc)
        {
            if (Library == null || currentProjectId == null) return;
            try
            {
                if (!TrySaveCurrent(true)) return;
                var version = Library.LoadVersion(currentProjectId, versionId);
                Library.Checkpoint(currentProjectId);
                version.ProjectId = currentProjectId;
                RememberProfileSection();
                LoadProject(version);
                RestoreProfileSection();
                SetDirty();
                if (TrySaveCurrent(true))
                    SetStatus("Открыта версия от " + ProfilesAutosave.LocalTime(savedUtc) + ". Состояние до восстановления сохранено в «Версии…».");
            }
            catch (Exception error) { ShowError("Версия не открыта: " + error.Message); }
        }

        /// <summary>Returns a deleted project to the library and to this drawing.</summary>
        internal void RestoreDeletedProject(string deletedId)
        {
            if (Library == null) return;
            try
            {
                if (!TrySaveCurrent(true)) return;
                var project = Library.RestoreDeleted(deletedId);
                libraryOnly.Remove(project.ProjectId);
                LoadProject(project);
                SetDirty();
                if (TrySaveCurrent(true, forceDrawing: true))
                    SetStatus("Проект «" + currentProjectName + "» восстановлен" + (saveProjectToDrawing != null ? " и записан в этот чертёж." : "."));
            }
            catch (Exception error) { ShowError("Проект не восстановлен: " + error.Message); }
        }
    }
}
