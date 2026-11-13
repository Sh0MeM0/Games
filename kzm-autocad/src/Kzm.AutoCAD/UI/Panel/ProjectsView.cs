using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Проекты чертежа: создать новый (название), открыть существующий, переименовать открытый, закрыть.
    /// Проект — набор поверхностей и картограмм внутри этого DWG.
    /// </summary>
    internal sealed class ProjectsView : UserControl, IPanelView
    {
        private readonly Label current = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly ListBox list = new ListBox { Width = 320, Height = 120, IntegralHeight = false };
        private readonly TextBox newName = new TextBox { Width = 220 };
        private readonly TextBox rename = new TextBox { Width = 220 };
        private readonly Button open = new Button { Text = "Открыть", AutoSize = true };
        private readonly FlowLayoutPanel renamePanel;
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
