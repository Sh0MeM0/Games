using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>Table of the points of one surface; the elevation is edited right in the table.</summary>
    internal sealed class PointsView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        private readonly CheckBox onlyProblems = new CheckBox { Text = "Только с ошибкой отметки", AutoSize = true };
        private readonly DataGridView grid = new DataGridView();
        private readonly DataTable table = new DataTable();
        private KzmPanel panel;
        private string surfaceId;
        private Dictionary<long, SurfacePoint> points = new Dictionary<long, SurfacePoint>();

        internal PointsView()
        {
            table.Columns.Add("Key", typeof(long));
            table.Columns.Add("Номер", typeof(string));
            table.Columns.Add("X", typeof(string));
            table.Columns.Add("Y", typeof(string));
            table.Columns.Add("Отметка", typeof(string));
            table.Columns.Add("Источник", typeof(string));
            table.Columns.Add("XY", typeof(string));
            table.Columns.Add("Ошибка", typeof(string));

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            grid.DataSource = table;
            grid.DataBindingComplete += (s, e) =>
            {
                grid.Columns["Key"].Visible = false;
                foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = column.Name != "Отметка" && column.Name != "Номер";
                grid.Columns["Отметка"].DefaultCellStyle.BackColor = Color.LightYellow;
                grid.Columns["Номер"].DefaultCellStyle.BackColor = Color.LightYellow;
                foreach (DataGridViewRow row in grid.Rows)
                    if (!string.IsNullOrEmpty(row.Cells["Ошибка"].Value as string)) row.DefaultCellStyle.ForeColor = Color.Firebrick;
            };
            grid.CellValidating += ValidateElevation;
            grid.CellEndEdit += (s, e) => SavePoint(e.RowIndex, grid.Columns[e.ColumnIndex].Name);
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ShowSelected(); };

            onlyProblems.CheckedChanged += (s, e) => Fill();
            var show = new Button { Text = "Показать", AutoSize = true };
            show.Click += (s, e) => ShowSelected();
            var delete = new Button { Text = "Удалить выбранные", AutoSize = true };
            delete.Click += (s, e) => DeleteSelected();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            bar.Controls.AddRange(new Control[] { onlyProblems, show, delete });
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                Text = "Отметку и номер можно исправить прямо в таблице (жёлтые столбцы). Двойной щелчок — показать точку на чертеже.",
                Padding = new Padding(3),
            };
            Controls.Add(grid);
            Controls.Add(bar);
            Controls.Add(hint);
            Controls.Add(title);
            title.Dock = DockStyle.Top;
        }

        public void Bind(KzmPanel owner, string id)
        {
            panel = owner;
            surfaceId = id;
            var surface = panel.Snapshot.Find(id);
            if (surface == null || panel.Document == null)
            {
                title.Text = "Поверхность не найдена";
                points.Clear();
                table.Rows.Clear();
                return;
            }
            title.Text = "Точки поверхности «" + surface.Info.Name + "»: " + surface.Points;
            points = DrawingData.ReadPoints(panel.Document, panel.Snapshot.Settings, id)
                .ToDictionary(point => point.Id.Handle.Value, point => point);
            Fill();
        }

        private void Fill()
        {
            int decimals = panel?.Snapshot.Settings.Decimals ?? KzmSettings.DefaultDecimals;
            table.BeginLoadData();
            table.Rows.Clear();
            foreach (var point in points.Values.OrderBy(point => NumberKey(point.Number)).ThenBy(point => point.Number, StringComparer.Ordinal))
            {
                if (onlyProblems.Checked && point.Elevation.IsValid) continue;
                table.Rows.Add(point.Id.Handle.Value, point.Number ?? string.Empty,
                    ElevationFormat.Format(point.Position.X, 3), ElevationFormat.Format(point.Position.Y, 3),
                    point.Elevation.IsValid ? ElevationFormat.Format(point.Elevation.Value.Value, decimals) : string.Empty,
                    SourceKinds.Code(point.Data.Source) + (string.IsNullOrEmpty(point.Data.SourceHandle) ? string.Empty : " " + point.Data.SourceHandle),
                    XyModes.Title(point.Data.XyMode),
                    point.Elevation.Problem ?? string.Empty);
            }
            table.EndLoadData();
        }

        private static long NumberKey(string number) =>
            long.TryParse(number, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : long.MaxValue;

        private SurfacePoint PointOf(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return null;
            object key = grid.Rows[rowIndex].Cells["Key"].Value;
            return key is long value && points.TryGetValue(value, out var point) ? point : null;
        }

        private void ValidateElevation(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (grid.Columns[e.ColumnIndex].Name != "Отметка") return;
            string text = Convert.ToString(e.FormattedValue);
            if (!ElevationFormat.TryParse(text, out _))
            {
                PanelMessages.Error("введите отметку числом, например 145,32.");
                e.Cancel = true;
            }
        }

        private void SavePoint(int rowIndex, string column)
        {
            var point = PointOf(rowIndex);
            if (point == null) return;
            var settings = panel.Snapshot.Settings;
            var id = point.Id;
            if (column == "Номер")
            {
                string number = Convert.ToString(grid.Rows[rowIndex].Cells["Номер"].Value)?.Trim() ?? string.Empty;
                if (number == (point.Number ?? string.Empty)) return;
                PanelActions.Run("номер точки", document =>
                {
                    EditServices.SetPoint(document.Database, id, null, number, settings);
                    PanelMessages.Info("Номер точки " + id.Handle + " = «" + number + "».");
                }, panel.RefreshAll);
                return;
            }
            if (column != "Отметка") return;
            if (!ElevationFormat.TryParse(Convert.ToString(grid.Rows[rowIndex].Cells["Отметка"].Value), out double value)) return;
            if (point.Elevation.IsValid && Math.Abs(point.Elevation.Value.Value - value) < 1e-9) return;
            PanelActions.Run("отметка точки", document =>
            {
                EditServices.SetPoint(document.Database, id, value, null, settings);
                PanelMessages.Info("Отметка точки " + id.Handle + " = " + ElevationFormat.Format(value, settings.Decimals) + ".");
            }, panel.RefreshAll);
        }

        private List<SurfacePoint> Selected() =>
            grid.SelectedRows.Cast<DataGridViewRow>().Select(row => PointOf(row.Index)).Where(point => point != null).ToList();

        private void ShowSelected()
        {
            var selected = Selected();
            if (selected.Count == 0 && grid.CurrentRow != null) selected.Add(PointOf(grid.CurrentRow.Index));
            selected.RemoveAll(point => point == null);
            if (selected.Count == 0) return;
            PanelActions.Show(panel.Document, selected.Select(point => point.Id),
                KzmCommands.ZoomSize(panel.Snapshot.Find(surfaceId)?.Info.Appearance.TextHeight));
        }

        private void DeleteSelected()
        {
            var selected = Selected();
            if (selected.Count == 0) return;
            if (MessageBox.Show(this, "Удалить выбранные точки (" + selected.Count + ")?\nОтменить можно командой U.", "КЗМ",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var ids = selected.Select(point => point.Id).ToList();
            PanelActions.Run("удаление точек", document =>
            {
                int erased = EditServices.ErasePoints(document.Database, ids, panel.Snapshot.Settings);
                PanelMessages.Info("Удалено точек: " + erased + ".");
            }, panel.RefreshAll);
        }
    }
}
