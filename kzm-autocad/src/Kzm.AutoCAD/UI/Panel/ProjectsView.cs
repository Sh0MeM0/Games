using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Проекты чертежа: создать новый (название), открыть существующий, переименовать открытый, закрыть.
    /// Проект — набор поверхностей и картограмм внутри этого DWG. Ниже — проекты других чертежей (замечание 35):
    /// любой можно перенести в этот чертёж или открыть его чертёж.
    /// </summary>
    internal sealed class ProjectsView : UserControl, IPanelView
    {
        private readonly Label current = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly ListBox list = new ListBox { Width = 320, Height = 120, IntegralHeight = false };
        private readonly TextBox newName = new TextBox { Width = 220 };
        private readonly TextBox rename = new TextBox { Width = 220 };
        private readonly Button open = new Button { Text = "Открыть", AutoSize = true };
        private readonly FlowLayoutPanel renamePanel;
        /// <summary>Проекты других чертежей из общего списка пользователя.</summary>
        private readonly ListBox others = new ListBox { Width = 330, Height = 130, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Label othersNote = new Label { AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText };
        private KzmPanel panel;

        internal ProjectsView()
        {
            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6),
            };
            Label Header(string text) => new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) };
            FlowLayoutPanel Flow(params Control[] controls)
            {
                var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(350, 0) };
                flow.Controls.AddRange(controls);
                return flow;
            }
            root.Controls.Add(new Label { Text = "Проекты чертежа", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10, FontStyle.Bold) });
            root.Controls.Add(current);

            root.Controls.Add(Header("Новый проект"));
            var create = new Button { Text = "Создать", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            create.Click += (s, e) => Create();
            newName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Create(); e.SuppressKeyPress = true; } };
            root.Controls.Add(Flow(new Label { Text = "Название", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, newName, create));

            root.Controls.Add(Header("Открыть проект"));
            root.Controls.Add(list);
            open.Click += (s, e) => Open();
            list.DoubleClick += (s, e) => Open();
            root.Controls.Add(open);

            renamePanel = Flow(new Label { Text = "Новое название", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, rename, Button("Переименовать", Rename));
            root.Controls.Add(Header("Открытый проект"));
            root.Controls.Add(renamePanel);
            root.Controls.Add(Button("Закрыть проект", Close));
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3),
                Text = "Проект — набор поверхностей и картограмм внутри этого чертежа; всё хранится в DWG. В панели виден только открытый проект, " +
                       "объекты других проектов на чертеже остаются. Закрыть проект можно в любой момент и потом открыть снова — данные на месте.",
            });

            root.Controls.Add(Header("Проекты в других чертежах"));
            root.Controls.Add(others);
            others.DoubleClick += (s, e) => Transfer();
            root.Controls.Add(Flow(Button("Перенести в этот чертёж", Transfer), Button("Открыть тот чертёж", OpenDrawing)));
            root.Controls.Add(Flow(Button("Взять из файла…", Browse), Button("Убрать из списка", Forget)));
            root.Controls.Add(othersNote);
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3),
                Text = "Здесь проекты всех чертежей, которые вы открывали или сохраняли с модулем (начиная с версии 0.10; более старые " +
                       "чертежи появятся, когда вы их откроете, или выберите файл кнопкой «Взять из файла…»).\n" +
                       "«Перенести» копирует в этот чертёж всё из проекта: точки, границы, структурные линии, картограммы с подписями, " +
                       "таблицами и ведомостями. Берётся сохранённый файл, сам он не меняется. Одна команда U отменяет перенос.",
            });
            Controls.Add(root);
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (s, e) => action();
            return button;
        }

        public void Bind(KzmPanel owner, string id)
        {
            panel = owner;
            var snapshot = panel.Snapshot;
            current.Text = panel.Document == null ? "Нет открытого чертежа."
                : snapshot.Project == null ? "Проект не открыт: создайте новый или откройте один из списка."
                : "Открыт проект «" + snapshot.Project.Name + "».";
            current.ForeColor = snapshot.Project == null ? Color.DarkOrange : SystemColors.ControlText;
            list.Items.Clear();
            foreach (var project in snapshot.Projects) list.Items.Add(new ProjectItem(project, snapshot.Project?.Id == project.Id));
            open.Enabled = list.Items.Count > 0;
            renamePanel.Enabled = snapshot.Project != null;
            rename.Text = snapshot.Project?.Name ?? string.Empty;
            if (newName.Text.Length == 0) newName.Text = "Проект " + (snapshot.Projects.Count + 1).ToString(CultureInfo.InvariantCulture);
            ShowOthers(null);
        }

        private string CurrentPath()
        {
            string path = panel?.Document?.Database.Filename;
            return ProjectCatalogFile.IsDrawingFile(path) ? Path.GetFullPath(path) : null;
        }

        /// <summary>Проекты других чертежей; <paramref name="select"/> — выделить этот (после «Взять из файла…»).</summary>
        private void ShowOthers(CatalogEntry select)
        {
            var entries = ProjectCatalog.Others(ProjectCatalogFile.Load(), CurrentPath());
            others.BeginUpdate();
            others.Items.Clear();
            foreach (var entry in entries) others.Items.Add(new OtherItem(entry));
            others.EndUpdate();
            if (select != null)
                for (int i = 0; i < others.Items.Count; i++)
                    if (others.Items[i] is OtherItem item && item.Entry.ProjectId == select.ProjectId && ProjectCatalog.SamePath(item.Entry.Path, select.Path))
                        others.SelectedIndex = i;
            othersNote.Text = entries.Count == 0
                ? "Других проектов пока нет. Они появятся, когда вы сохраните или откроете чертёж с проектом; можно и выбрать файл."
                : "Проектов в других чертежах: " + entries.Count + ". Двойной щелчок — перенести в этот чертёж.";
        }

        private sealed class OtherItem
        {
            internal OtherItem(CatalogEntry entry) { Entry = entry; }
            internal CatalogEntry Entry { get; }

            public override string ToString()
            {
                string date = DateTime.TryParseExact(Entry.Seen, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var seen)
                    ? seen.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : Entry.Seen;
                bool exists = File.Exists(Entry.Path);
                return Entry.Name + " — " + Path.GetFileName(Entry.Path) + ", " + date + " (поверхностей " + Entry.Surfaces + ", картограмм " + Entry.Cartograms + ")" +
                       (exists ? string.Empty : " — файл не найден");
            }
        }

        private CatalogEntry Chosen()
        {
            if (others.SelectedItem is OtherItem item) return item.Entry;
            PanelMessages.Info("Выберите проект в списке «Проекты в других чертежах».");
            return null;
        }

        /// <summary>«Перенести в этот чертёж»: копия проекта из сохранённого файла; одна команда — одна отмена.</summary>
        private void Transfer()
        {
            var entry = Chosen();
            if (entry == null || panel?.Document == null) return;
            if (!File.Exists(entry.Path)) { PanelMessages.Error("файл «" + entry.Path + "» не найден — перемещён или удалён? Уберите его из списка."); return; }
            if (MessageBox.Show(this, "Перенести проект «" + entry.Name + "» из чертежа\n" + entry.Path + "\nв текущий чертёж?\n\n" +
                                      "Копируются точки, границы, структурные линии и картограммы проекта (с оформлением). Если тот чертёж открыт и не " +
                                      "сохранён, переносится сохранённое состояние.", "КЗМ", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string message = null;
            PanelActions.Run("перенос проекта", document => message = ProjectTransfer.Import(document, entry.Path, entry.ProjectId), () =>
            {
                if (message != null) PanelMessages.Info(message);
                panel.AfterProjectChange();
            });
        }

        /// <summary>«Открыть тот чертёж»: уже открытый — становится текущим.</summary>
        private void OpenDrawing()
        {
            var entry = Chosen();
            if (entry == null) return;
            var documents = AcadApplication.DocumentManager;
            foreach (Document document in documents)
            {
                if (ProjectCatalogFile.IsDrawingFile(document.Database.Filename) && ProjectCatalog.SamePath(Path.GetFullPath(document.Database.Filename), entry.Path))
                {
                    documents.MdiActiveDocument = document;
                    return;
                }
            }
            if (!File.Exists(entry.Path)) { PanelMessages.Error("файл «" + entry.Path + "» не найден."); return; }
            try
            {
                documents.Open(entry.Path, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
            {
                PanelMessages.Error("чертёж не открылся: " + exception.Message);
            }
        }

        /// <summary>«Взять из файла…»: любой DWG — его проекты попадают в список, их можно перенести.</summary>
        private void Browse()
        {
            using (var dialog = new OpenFileDialog { Filter = "Чертежи AutoCAD (*.dwg)|*.dwg", Title = "Чертёж с проектом КЗМ" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var projects = ProjectTransfer.ReadProjects(dialog.FileName);
                    if (projects.Count == 0) { PanelMessages.Info("В чертеже «" + Path.GetFileName(dialog.FileName) + "» нет проектов КЗМ."); return; }
                    ProjectCatalogFile.Register(dialog.FileName, projects);
                    ShowOthers(new CatalogEntry { ProjectId = projects[0].ProjectId, Path = Path.GetFullPath(dialog.FileName) });
                    PanelMessages.Info("Проектов в «" + Path.GetFileName(dialog.FileName) + "»: " + projects.Count + " — выберите нужный и нажмите «Перенести в этот чертёж».");
                }
                catch (KzmException exception)
                {
                    PanelMessages.Error(exception.Message);
                }
            }
        }

        private void Forget()
        {
            var entry = Chosen();
            if (entry == null) return;
            ProjectCatalogFile.Forget(entry);
            ShowOthers(null);
        }

        private sealed class ProjectItem
        {
            internal ProjectItem(ProjectInfo project, bool open) { Project = project; IsOpen = open; }
            internal ProjectInfo Project { get; }
            internal bool IsOpen { get; }
            public override string ToString() => Project.Name + (IsOpen ? "   (открыт)" : string.Empty);
        }

        private void Create()
        {
            if (panel?.Document == null) return;
            string name;
            try { name = SurfaceNames.Validate(newName.Text); }
            catch (KzmException exception) { PanelMessages.Error(exception.Message.Replace("Имя поверхности", "Название проекта")); return; }
            if (panel.Snapshot.Projects.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                PanelMessages.Error("проект «" + name + "» уже есть — откройте его из списка.");
                return;
            }
            var project = new ProjectInfo { Id = Guid.NewGuid().ToString("N"), Name = name, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
            PanelActions.Run("новый проект", document =>
            {
                EditServices.CreateProject(document.Database, project);
                PanelMessages.Info("Создан и открыт проект «" + name + "». Дальше — поверхности (шаг 1 «Порядка работы»).");
            }, () =>
            {
                newName.Text = string.Empty;
                panel.AfterProjectChange();
            });
        }

        private void Open()
        {
            if (!(list.SelectedItem is ProjectItem item)) { PanelMessages.Info("Выберите проект в списке."); return; }
            panel.OpenProject(item.Project.Id);
        }

        private void Rename()
        {
            var project = panel?.Snapshot.Project;
            if (project == null) return;
            string name;
            try { name = SurfaceNames.Validate(rename.Text); }
            catch (KzmException exception) { PanelMessages.Error(exception.Message.Replace("Имя поверхности", "Название проекта")); return; }
            string id = project.Id;
            PanelActions.Run("переименование проекта", document =>
            {
                EditServices.RenameProject(document.Database, id, name);
                PanelMessages.Info("Проект переименован: «" + name + "».");
            }, panel.AfterProjectChange);
        }

        private void Close() => panel?.CloseProject();
    }
}
