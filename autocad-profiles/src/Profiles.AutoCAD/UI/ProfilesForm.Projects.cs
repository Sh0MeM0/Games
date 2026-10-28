using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private readonly ComboBox projectPicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(240), Margin = new Padding(2, 3, 6, 3) };
        private readonly ComboBox templatePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Px(170), Margin = new Padding(2, 3, 6, 3) };
        private readonly CheckBox autoSaveJson = new CheckBox { Text = "Автосохранять JSON", AutoSize = true, Enabled = false, Margin = new Padding(8, 9, 8, 4) };
        private readonly Timer autoSaveTimer = new Timer { Interval = 700 };
        private readonly List<ProfileProject> projects = new List<ProfileProject>();
        private readonly Dictionary<string, string> jsonPaths = new Dictionary<string, string>();
        private readonly HashSet<string> jsonAutoSaveProjects = new HashSet<string>();
        private readonly Dictionary<string, string> lastWrittenJson = new Dictionary<string, string>();
        private readonly HashSet<string> memoryOnlyProjects = new HashSet<string>();
        private readonly Action<ProfileProject> saveProjectToDrawing;
        private readonly Action<string> deleteProjectFromDrawing;
        private string currentProjectId;
        private string currentProjectName;
        private bool saving;
        private string lastSaveError;

        private sealed class ProjectChoice
        {
            internal string Id;
            internal string Name;
            public override string ToString() => Name;
        }

        private sealed class TemplateChoice
        {
            internal ProfileTemplate Value;
            internal string Name;
            public override string ToString() => Name;
        }

        private ProfileTemplate SelectedTemplate => templatePicker.SelectedItem is TemplateChoice item ? item.Value : ProfileTemplate.Reconstruction;

        private Control BuildProjectBar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
            bar.Controls.Add(Caption("Проект в чертеже"));
            bar.Controls.Add(projectPicker);
            bar.Controls.Add(Caption("Тип профиля"));
            bar.Controls.Add(templatePicker);
            bar.Controls.Add(Button("Новый проект", NewProject));
            bar.Controls.Add(Button("Переименовать", RenameProject));
            var save = Button("Сохранить в DWG", SaveDraftExplicit);
            save.Enabled = saveProjectToDrawing != null;
            bar.Controls.Add(save);
            var delete = Button("Удалить проект…", DeleteProject);
            bar.Controls.Add(delete);
            projectPicker.AccessibleName = "Проект в чертеже";
            templatePicker.AccessibleName = "Тип профиля";
            tips.SetToolTip(delete, "Удалить текущий проект (все его вкладки и поперечники) из чертежа. Уже построенные объекты остаются в DWG как обычные линии и тексты.");
            tips.SetToolTip(projectPicker, "Независимые наборы исходных данных. Каждый проект может иметь продольный профиль и набор поперечников.");
            tips.SetToolTip(templatePicker, "Выбор другого типа создаёт новую пустую вкладку профиля. Данные исходной вкладки сохраняются; их можно открыть снова.");
            tips.SetToolTip(save, "Данные записываются в текущий чертёж (Ctrl+S). После закрытия окна сохраните сам DWG, чтобы оставить изменения на диске.");
            tips.SetToolTip(autoSaveJson, "По умолчанию JSON не переписывается автоматически. Сначала импортируйте или экспортируйте файл, затем включите эту опцию явно.");
            return bar;
        }

        private void InitializeProjectSupport(IReadOnlyList<ProfileProject> available)
        {
            templatePicker.Items.Add(new TemplateChoice { Value = ProfileTemplate.Reconstruction, Name = "Подчистка канала" });
            templatePicker.Items.Add(new TemplateChoice { Value = ProfileTemplate.NewChannel, Name = "Новый канал" });
            templatePicker.Items.Add(new TemplateChoice { Value = ProfileTemplate.Nvk, Name = "Сети НВК" });
            templatePicker.SelectedIndex = 0;
            templatePicker.SelectionChangeCommitted += TemplateSelectionChanged;
            if (available != null)
                foreach (var project in available) RegisterProject(project);
            projectPicker.SelectionChangeCommitted += ProjectSelectionChanged;
            autoSaveJson.CheckedChanged += (s, e) =>
            {
                if (loading || currentProjectId == null) return;
                if (autoSaveJson.Checked) jsonAutoSaveProjects.Add(currentProjectId);
                else jsonAutoSaveProjects.Remove(currentProjectId);
                SetDirty();
            };
            autoSaveTimer.Tick += (s, e) =>
            {
                autoSaveTimer.Stop();
                if (!dirty || loading || saving || HasActiveNumericEditor || (stationInputEdited && sectionStation.ContainsFocus)) return;
                if (saveProjectToDrawing == null && !(autoSaveJson.Checked && currentFile != null)) return;
                TrySaveCurrent(false);
            };
        }

        private void SelectTemplate(ProfileTemplate template)
        {
            for (int i = 0; i < templatePicker.Items.Count; i++)
                if (((TemplateChoice)templatePicker.Items[i]).Value == template) { templatePicker.SelectedIndex = i; break; }
            ApplyTemplateColumns();
        }

        private void ApplyTemplateColumns()
        {
            string[] visible;
            if (SelectedTemplate == ProfileTemplate.Nvk)
                visible = new[] { "Station", "Ground", "ProjectGround", "PipeInvert", "PipeDescription", "Foundation", "PointName" };
            else if (SelectedTemplate == ProfileTemplate.NewChannel)
                visible = new[] { "Station", "Ground", "ProjectBottom", "PeatDepth", "BottomWidth", "LeftSlope", "RightSlope" };
            else
                visible = new[] { "Station", "LeftBank", "RightBank", "ExistingBottom", "Water", "ProjectBottom", "BottomWidth", "LeftSlope", "RightSlope" };
            visible = new[] { "FillMissing" }.Concat(visible).ToArray();
            foreach (DataGridViewColumn column in longGrid.Columns) column.Visible = visible.Contains(column.Name);
            for (int i = 0; i < visible.Length; i++) longGrid.Columns[visible[i]].DisplayIndex = i;
            longGrid.Columns["Ground"].HeaderText = SelectedTemplate == ProfileTemplate.Nvk ? "Натурная земля, м" : "Земля, м";
            longGrid.Invalidate();
        }

        private void RegisterProject(ProfileProject project)
        {
            if (project == null) return;
            var clone = ProjectFile.Clone(project);
            int index = projects.FindIndex(p => string.Equals(p.ProjectId, clone.ProjectId, StringComparison.Ordinal));
            if (index < 0) projects.Add(clone);
            else projects[index] = clone;
        }

        private void RefreshProjectPicker()
        {
            bool old = loading;
            loading = true;
            projectPicker.Items.Clear();
            foreach (var item in projects)
                projectPicker.Items.Add(new ProjectChoice { Id = item.ProjectId, Name = item.Name });
            for (int i = 0; i < projectPicker.Items.Count; i++)
                if (((ProjectChoice)projectPicker.Items[i]).Id == currentProjectId) { projectPicker.SelectedIndex = i; break; }
            loading = old;
        }

        private void RestoreJsonSelection()
        {
            bool old = loading;
            loading = true;
            currentFile = currentProjectId != null && jsonPaths.TryGetValue(currentProjectId, out var path) ? path : null;
            autoSaveJson.Enabled = currentFile != null;
            autoSaveJson.Checked = currentFile != null && jsonAutoSaveProjects.Contains(currentProjectId);
            loading = old;
        }

        private void ProjectSelectionChanged(object sender, EventArgs e)
        {
            if (loading || !(projectPicker.SelectedItem is ProjectChoice choice) || choice.Id == currentProjectId) return;
            string requested = choice.Id;
            if (!TrySaveCurrent(true)) { RefreshProjectPicker(); return; }
            var target = projects.FirstOrDefault(p => p.ProjectId == requested);
            if (target == null) { RefreshProjectPicker(); return; }
            LoadProject(ProjectFile.Clone(target));
            SetStatus("Открыт проект «" + currentProjectName + "». Данные проектов хранятся отдельно.");
        }

        private void NewProject()
        {
            if (!TrySaveCurrent(true)) return;
            string name = AskProjectName("Новый проект", UniqueProjectName("Проект"));
            if (name == null) return;
            LoadProject(BlankProject(name));
            SetDirty();
            TrySaveCurrent(true);
        }

        private ProfileProject BlankProject(string name) => new ProfileProject
        {
            ProjectId = Guid.NewGuid().ToString("N"), Name = name, Kind = activeKind, Template = SelectedTemplate,
            Settings = new ProfileSettings
            {
                Title = "ПРОДОЛЬНЫЙ ПРОФИЛЬ",
                HorizontalScale = 1000, VerticalScale = 100,
                CrossHorizontalScale = 100, CrossVerticalScale = 100,
                MetresPerDrawingUnit = 1, TextHeightMm = 2.5, TextWidthFactor = .85
            },
            Sections = new List<CrossSection> { NewSection(1) }
        };

        private void DeleteProject()
        {
            if (currentProjectId == null) return;
            string deletedId = currentProjectId, deletedName = currentProjectName ?? "Проект";
            if (MessageBox.Show(this,
                "Удалить проект «" + deletedName + "»" + (deleteProjectFromDrawing != null ? " из чертежа" : "") + "?\n\n" +
                "Будут удалены его исходные данные: все вкладки профилей, таблицы и поперечники. " +
                "Уже построенные линии и тексты останутся в чертеже как обычные объекты — при необходимости удалите их вручную.\n\n" +
                "Если проект может понадобиться, сначала нажмите «Отмена» и экспортируйте его в JSON.",
                "Удаление проекта", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            autoSaveTimer.Stop();
            interpolationTimer.Stop();
            try { deleteProjectFromDrawing?.Invoke(deletedId); }
            catch (Exception ex) { ShowError("Проект не удалён: " + ex.Message); return; }
            projects.RemoveAll(project => string.Equals(project.ProjectId, deletedId, StringComparison.OrdinalIgnoreCase));
            jsonPaths.Remove(deletedId);
            jsonAutoSaveProjects.Remove(deletedId);
            lastWrittenJson.Remove(deletedId);
            memoryOnlyProjects.Remove(deletedId);
            foreach (string key in profileSectionSelections.Keys.Where(key => key.StartsWith(deletedId + ":", StringComparison.OrdinalIgnoreCase)).ToList())
                profileSectionSelections.Remove(key);
            var next = projects.FirstOrDefault();
            // The deleted editor contents must not be autosaved back: load the
            // next project (or an empty one) without reading the old buffer.
            LoadProject(next != null ? ProjectFile.Clone(next) : BlankProject(UniqueProjectName("Проект")));
            SetStatus("Проект «" + deletedName + "» удалён. Открыт проект «" + currentProjectName + "»." +
                (deleteProjectFromDrawing != null ? " Сохраните DWG, чтобы удаление сохранилось на диске." : ""));
        }

        private void RenameProject()
        {
            string name = AskProjectName("Переименовать проект", currentProjectName ?? "Проект");
            if (name == null || name == currentProjectName) return;
            currentProjectName = name;
            SetDirty();
            TrySaveCurrent(true);
        }

        private string UniqueProjectName(string prefix)
        {
            int number = 1;
            while (projects.Any(p => string.Equals(p.Name, prefix + " " + number, StringComparison.OrdinalIgnoreCase))) number++;
            return prefix + " " + number;
        }

        private string AskProjectName(string caption, string initial, string fieldCaption = "Название проекта", int maximumLength = 100)
        {
            using (var dialog = new Form
            {
                Text = caption, Font = Font, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
            })
            {
                // Auto-sized layout: absolute positions overlapped at 125–150 % Windows scaling.
                var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 4, Padding = new Padding(Px(12)) };
                var input = new TextBox { Text = initial, Width = Px(400), MaxLength = maximumLength, AccessibleName = fieldCaption };
                var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(Px(400), 0) };
                var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, Px(6), 0, 0) };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Px(90), 0) };
                var ok = new Button { Text = "Сохранить", AutoSize = true, MinimumSize = new Size(Px(90), 0) };
                ok.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Any(char.IsControl))
                    { error.Text = "Введите название без управляющих символов."; input.Focus(); return; }
                    dialog.DialogResult = DialogResult.OK;
                };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                layout.Controls.Add(new Label { Text = fieldCaption, AutoSize = true, Margin = new Padding(0, 0, 0, Px(4)) }, 0, 0);
                layout.Controls.Add(input, 0, 1);
                layout.Controls.Add(error, 0, 2);
                layout.Controls.Add(buttons, 0, 3);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => { input.Focus(); input.SelectAll(); };
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
            }
        }

        private void PersistProject(ProfileProject project, bool explicitSave)
        {
            // Clone performs numeric/schema validation without requiring complete geometry.
            var snapshot = ProjectFile.Clone(project);
            if (saveProjectToDrawing != null) saveProjectToDrawing(snapshot);
            if (autoSaveJson.Checked && currentFile != null)
            {
                ProjectFile.Save(currentFile, snapshot);
                lastWrittenJson[snapshot.ProjectId] = ProjectFile.Serialize(snapshot);
            }
            RegisterProject(snapshot);
            Project = snapshot;
            profileContainer = ProjectFile.Clone(snapshot);
            bool savedJson = lastWrittenJson.TryGetValue(snapshot.ProjectId, out var written) && written == ProjectFile.Serialize(snapshot);
            dirty = saveProjectToDrawing == null && !savedJson;
            if (dirty) memoryOnlyProjects.Add(snapshot.ProjectId);
            else memoryOnlyProjects.Remove(snapshot.ProjectId);
            lastSaveError = null;
            RefreshProjectPicker();
            UpdateTitle();
            if (saveProjectToDrawing != null)
                SetStatus("Данные проекта сохранены в DWG. После закрытия окна сохраните сам чертёж AutoCAD.");
            else if (autoSaveJson.Checked && currentFile != null)
                SetStatus("Автосохранено в JSON: " + Path.GetFileName(currentFile));
            else if (explicitSave)
                SetStatus("Данные сохранены в текущем окне. Для хранения между сеансами экспортируйте JSON.");
        }

        private bool TrySaveCurrent(bool showError)
        {
            if (loading || saving) return true;
            autoSaveTimer.Stop();
            saving = true;
            try
            {
                PersistProject(ReadProject(), showError);
                return true;
            }
            catch (Exception ex)
            {
                lastSaveError = ex.Message;
                dirty = true;
                UpdateTitle();
                SetStatus("Последние изменения не сохранены: " + ex.Message, true);
                if (showError) ShowError("Последние изменения не сохранены. Исправьте данные перед переключением проекта или сохранением.\n\n" + ex.Message);
                return false;
            }
            finally { saving = false; }
        }

        private void SaveDraftExplicit()
        {
            if (saveProjectToDrawing == null) { SaveProject(); return; }
            TrySaveCurrent(true);
        }

        private void OpenProject()
        {
            if (!TrySaveCurrent(true)) return;
            using (var dialog = new OpenFileDialog { Filter = "Данные профилей (*.json)|*.json|Все файлы (*.*)|*.*", Title = "Импортировать проект профилей", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var project = ProjectFile.Load(dialog.FileName);
                    if (projects.Any(p => p.ProjectId == project.ProjectId))
                    {
                        var answer = MessageBox.Show(this, "Проект с таким идентификатором уже есть в чертеже.\n\nДа — заменить его исходные данные.\nНет — импортировать отдельной копией.\nОтмена — не импортировать.",
                            "Импорт проекта", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                        if (answer == DialogResult.Cancel) return;
                        if (answer == DialogResult.No)
                        {
                            project.ProjectId = Guid.NewGuid().ToString("N");
                            project.Name += " — копия";
                        }
                    }
                    jsonPaths[project.ProjectId] = dialog.FileName;
                    jsonAutoSaveProjects.Remove(project.ProjectId);
                    LoadProject(project);
                    SetDirty();
                    if (TrySaveCurrent(true)) SetStatus("Импортирован проект «" + currentProjectName + "». Автозапись JSON выключена; исходный файл не изменён.");
                }
                catch (Exception ex) { ShowError("Не удалось импортировать файл: " + ex.Message); }
            }
        }

        private void SaveProject()
        {
            bool exported = false;
            try
            {
                var project = ReadProject();
                using (var dialog = new SaveFileDialog
                {
                    Filter = "Данные профилей (*.json)|*.json", Title = "Экспортировать проект профилей",
                    AddExtension = true, DefaultExt = "json", FileName = currentFile == null ? "Профили.json" : Path.GetFileName(currentFile),
                    InitialDirectory = currentFile == null ? "" : Path.GetDirectoryName(currentFile), OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    ProjectFile.Save(dialog.FileName, project);
                    exported = true;
                    lastWrittenJson[project.ProjectId] = ProjectFile.Serialize(project);
                    jsonPaths[currentProjectId] = dialog.FileName;
                    RestoreJsonSelection();
                    PersistProject(project, true);
                    SetStatus("Экспортирован JSON: " + Path.GetFileName(currentFile) + ". Автозапись файла включается отдельным флажком.");
                }
            }
            catch (Exception ex)
            {
                dirty = true;
                UpdateTitle();
                ShowError((exported ? "JSON экспортирован, но не удалось завершить сохранение данных проекта: " : "Не удалось экспортировать: ") + ex.Message);
            }
        }

        private void LoadExample()
        {
            if (!TrySaveCurrent(true)) return;
            var sample = activeKind == ProfileKind.Longitudinal ? SampleProjects.ForTemplate(SelectedTemplate) : SampleProjects.CrossSections();
            sample.Template = SelectedTemplate;
            sample.ProjectId = Guid.NewGuid().ToString("N");
            sample.Name = UniqueProjectName("Пример");
            LoadProject(sample);
            SetDirty();
            if (TrySaveCurrent(true)) SetStatus("Создан отдельный демонстрационный проект. Замените пример своими данными.");
        }

        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            autoSaveTimer.Stop();
            interpolationTimer.Stop();
            if (accepted) return;
            longGrid.EndEdit(); crossGrid.EndEdit();
            if (!TrySaveCurrent(false))
            {
                var answer = MessageBox.Show(this,
                    "Последние изменения не удалось сохранить:\n" + lastSaveError + "\n\nЗакрыть окно без сохранения этих изменений? Ранее сохранённые данные останутся.",
                    "Несохранённые изменения", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            }
            else if (saveProjectToDrawing == null && memoryOnlyProjects.Count > 0)
            {
                var answer = MessageBox.Show(this,
                    "В этом окне нет записи в DWG. Есть проекты, изменения которых хранятся только в памяти: " + memoryOnlyProjects.Count + ".\n\nДа — экспортировать текущий проект в JSON и остаться в окне.\nНет — закрыть без сохранения оставшихся изменений.\nОтмена — вернуться к данным.",
                    "Несохранённые проекты", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (answer != DialogResult.No)
                {
                    e.Cancel = true;
                    if (answer == DialogResult.Yes) SaveProject();
                    return;
                }
            }
            DialogResult = DialogResult.Cancel;
        }

        private void SetDirty()
        {
            if (loading || saving) return;
            dirty = true;
            Plan = null;
            preview.MarkStale();
            previewNotes.Text = "Данные изменены. Нажмите «Обновить» для повторной проверки.";
            UpdateTitle();
            SetStatus("Есть изменения. Корректные данные будут автоматически сохранены в DWG.");
            autoSaveTimer.Stop(); autoSaveTimer.Start();
            interpolationTimer.Stop(); interpolationTimer.Start();
        }

        private void UpdateTitle()
        {
            Text = "Профиля — " + (currentProjectName ?? "проект") + (dirty ? " *" : "");
        }
    }
}
