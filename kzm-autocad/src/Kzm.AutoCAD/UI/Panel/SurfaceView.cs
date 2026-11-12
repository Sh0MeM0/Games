using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>Properties of one surface, or the form of a new surface.</summary>
    internal sealed class SurfaceView : UserControl, IPanelView
    {
        private readonly Label title = new Label();
        private readonly TextBox name = new TextBox();
        private readonly RadioButton fact = new RadioButton { Text = "Фактическая", AutoSize = true, Checked = true };
        private readonly RadioButton project = new RadioButton { Text = "Проектная", AutoSize = true };
        private readonly ComboBox layer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckBox movePoints = new CheckBox { Text = "Перенести существующие точки на этот слой", AutoSize = true, Checked = true };
        private readonly Label info = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly Button save = new Button { AutoSize = true };
        private readonly Button makeCurrent = new Button { Text = "Сделать текущей", AutoSize = true };
        private readonly Button import = new Button { Text = "Импорт отметок…", AutoSize = true };
        private readonly Button delete = new Button { Text = "Удалить поверхность…", AutoSize = true };
        private KzmPanel panel;
        private SurfaceSummary surface;
        private bool isNew;

        internal SurfaceView()
        {
            AutoScroll = true;
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            title.AutoSize = true;
            table.Controls.Add(title, 0, 0);
            table.SetColumnSpan(title, 2);

            AddRow(table, "Имя", name);
            var kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            kinds.Controls.Add(fact);
            kinds.Controls.Add(project);
            AddRow(table, "Тип", kinds);
            AddRow(table, "Слой точек", layer);
            table.Controls.Add(movePoints);
            table.SetColumnSpan(movePoints, 2);
            table.Controls.Add(info);
            table.SetColumnSpan(info, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { save, makeCurrent, import, delete });
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);

            name.Anchor = layer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            fact.CheckedChanged += (s, e) => SwapDefaultLayer();
            layer.TextChanged += (s, e) => movePoints.Visible = !isNew && surface != null && surface.Points > 0 &&
                                                               !string.Equals(layer.Text.Trim(), surface.Info.Layer, StringComparison.OrdinalIgnoreCase);
            save.Click += (s, e) => Save();
            makeCurrent.Click += (s, e) => panel.SetCurrent(surface?.Info.Id);
            import.Click += (s, e) => panel.ShowImport(surface?.Info.Id);
            delete.Click += (s, e) => Delete();
        }

        private static void AddRow(TableLayoutPanel table, string caption, Control control)
        {
            table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) });
            table.Controls.Add(control);
        }

        internal void PrepareNew() => isNew = true;

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            string typedLayer = layer.Text;
            layer.Items.Clear();
            if (panel.Document != null)
                foreach (var item in DrawingData.Layers(panel.Document)) layer.Items.Add(item);
            layer.Text = typedLayer;
            if (surfaceId == null || isNew)
            {
                // A refresh of the panel keeps what the user is typing into the new-surface form.
                if (isNew || !creating)
                {
                    name.Text = string.Empty;
                    fact.Checked = true;
                    layer.Text = SurfaceInfo.DefaultLayer(SurfaceKind.Fact);
                }
                isNew = false;
                creating = true;
                surface = null;
                title.Text = "Новая поверхность";
                info.Text = "Сначала задаётся слой: в него попадут точки этой поверхности. " +
                            "Выберите существующий слой или введите новое имя.";
                save.Text = "Создать";
                save.Visible = true;
                movePoints.Visible = makeCurrent.Visible = import.Visible = delete.Visible = false;
                return;
            }
            creating = false;
            surface = panel.Snapshot.Find(surfaceId);
            if (surface == null)
            {
                title.Text = "Поверхность не найдена";
                info.Text = "Её нет в текущем чертеже (удалена или открыт другой чертёж).";
                save.Visible = movePoints.Visible = makeCurrent.Visible = import.Visible = delete.Visible = false;
                return;
            }
            title.Text = "Поверхность";
            name.Text = surface.Info.Name;
            (surface.Info.Kind == SurfaceKind.Project ? project : fact).Checked = true;
            layer.Text = surface.Info.Layer;
            save.Text = "Сохранить";
            save.Visible = true;
            movePoints.Visible = false;
            makeCurrent.Visible = !surface.IsCurrent;
            import.Visible = delete.Visible = true;
            info.Text = "Точек: " + surface.Points.ToString(CultureInfo.InvariantCulture) +
                        (surface.BrokenPoints > 0 ? "; с нечитаемой отметкой: " + surface.BrokenPoints : string.Empty) +
                        (surface.PendingPairs > 0 ? "; пар близких точек: " + surface.PendingPairs : string.Empty) +
                        (surface.IsCurrent ? "\nТекущая поверхность." : string.Empty) +
                        (string.IsNullOrEmpty(surface.Info.Created) ? string.Empty : "\nСоздана: " + surface.Info.Created);
        }

        private bool creating;

        private void SwapDefaultLayer()
        {
            var kind = project.Checked ? SurfaceKind.Project : SurfaceKind.Fact;
            var other = kind == SurfaceKind.Project ? SurfaceKind.Fact : SurfaceKind.Project;
            if (layer.Text.Trim().Length == 0 || string.Equals(layer.Text.Trim(), SurfaceInfo.DefaultLayer(other), StringComparison.OrdinalIgnoreCase))
                layer.Text = SurfaceInfo.DefaultLayer(kind);
        }

        private void Save()
        {
            string newName, newLayer;
            try
            {
                newName = SurfaceNames.Validate(name.Text);
                newLayer = layer.Text.Trim();
                if (newLayer.Length == 0) throw new KzmException("Укажите слой точек.");
            }
            catch (KzmException exception)
            {
                PanelMessages.Error(exception.Message);
                return;
            }
            string ownId = creating ? null : surface?.Info.Id;
            if (panel.Snapshot.Surfaces.Any(other => other.Info.Id != ownId &&
                    string.Equals(other.Info.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            {
                PanelMessages.Error("поверхность «" + newName + "» уже есть.");
                return;
            }
            var kind = project.Checked ? SurfaceKind.Project : SurfaceKind.Fact;
            if (creating)
            {
                var created = new SurfaceInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = newName,
                    Kind = kind,
                    Layer = newLayer,
                    Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                };
                PanelActions.Run("новая поверхность", document =>
                {
                    EditServices.CreateSurface(document.Database, created, true);
                    PanelMessages.Info("Создана поверхность " + created.Title + "; она текущая.");
                }, () => panel.AfterSurfaceChange(created.Id));
                return;
            }
            var updated = new SurfaceInfo
            {
                Id = surface.Info.Id,
                Name = newName,
                Kind = kind,
                Layer = newLayer,
                Created = surface.Info.Created,
                Revision = surface.Info.Revision,
            };
            bool move = movePoints.Visible && movePoints.Checked;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("свойства поверхности", document =>
            {
                int moved = EditServices.UpdateSurface(document.Database, updated, move, settings);
                PanelMessages.Info("Сохранено: " + updated.Title + (moved > 0 ? "; точек перенесено на слой: " + moved : string.Empty) + ".");
            }, () => panel.AfterSurfaceChange(updated.Id));
        }

        private void Delete()
        {
            if (surface == null) return;
            var answer = MessageBox.Show(this,
                "Удалить поверхность «" + surface.Info.Name + "» и все её точки (" + surface.Points + ")?\n\nОтменить можно командой U (ОТМЕНИТЬ).",
                "КЗМ", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            string id = surface.Info.Id;
            string title = surface.Info.Name;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("удаление поверхности", document =>
            {
                int erased = EditServices.DeleteSurface(document.Database, id, settings);
                PanelMessages.Info("Поверхность «" + title + "» удалена, точек удалено: " + erased + ".");
            }, () => panel.AfterSurfaceChange(null));
        }
    }
}
