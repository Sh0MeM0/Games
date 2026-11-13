using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Таблица точек поверхности: поиск, сортировка по любому столбцу, правка номера и отметки
    /// в ячейке, исключение из триангуляции (задание п. 3), сдвиг отметок выбранных точек.
    /// </summary>
    internal sealed class PointsView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        private readonly TextBox search = new TextBox { Width = 120 };
        private readonly CheckBox onlyProblems = new CheckBox { Text = "Только с ошибкой", AutoSize = true };
        private readonly TextBox shift = new TextBox { Width = 55, Text = "0,10" };
        private readonly Label count = new Label { AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
        private readonly DataGridView grid = new DataGridView();
        private readonly DataTable table = new DataTable();
        private KzmPanel panel;
        private string surfaceId;
        private bool filling;
        private Dictionary<long, SurfacePoint> points = new Dictionary<long, SurfacePoint>();

        internal PointsView()
        {
            // Invariant: the search converts elevations to text with a dot.
            table.Locale = CultureInfo.InvariantCulture;
            table.Columns.Add("Key", typeof(long));
            table.Columns.Add("NumKey", typeof(long));
            table.Columns.Add("Номер", typeof(string));
            table.Columns.Add("Искл.", typeof(bool));
            table.Columns.Add("X", typeof(double));
            table.Columns.Add("Y", typeof(double));
            table.Columns.Add("Отметка", typeof(double));
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
            grid.DataBindingComplete += (s, e) => Decorate();
            grid.CellFormatting += FormatCell;
            grid.CellParsing += ParseCell;
            grid.DataError += (s, e) =>
            {
                PanelMessages.Error("введите число, например 145,32.");
                e.ThrowException = false;
            };
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += (s, e) => { if (!filling && e.RowIndex >= 0) SavePoint(e.RowIndex, grid.Columns[e.ColumnIndex].Name); };
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].ReadOnly) ShowSelected(); };
            grid.ColumnHeaderMouseClick += (s, e) => SortBy(grid.Columns[e.ColumnIndex].Name);
            grid.SelectionChanged += (s, e) => count.Text = grid.SelectedRows.Count > 1 ? "выбрано: " + grid.SelectedRows.Count : string.Empty;

            search.TextChanged += (s, e) => ApplyFilter();
            onlyProblems.CheckedChanged += (s, e) => ApplyFilter();
            var show = new Button { Text = "Показать", AutoSize = true };
            show.Click += (s, e) => ShowSelected();
            var exclude = new Button { Text = "Исключить", AutoSize = true };
            exclude.Click += (s, e) => SetExcluded(true);
            var include = new Button { Text = "Вернуть", AutoSize = true };
            include.Click += (s, e) => SetExcluded(false);
            var move = new Button { Text = "Сдвинуть отметки на", AutoSize = true };
            move.Click += (s, e) => ShiftSelected();
            var delete = new Button { Text = "Удалить выбранные", AutoSize = true };
            delete.Click += (s, e) => DeleteSelected();
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            bar.Controls.AddRange(new Control[]
            {
                new Label { Text = "Поиск", AutoSize = true, Margin = new Padding(3, 7, 0, 0) }, search, onlyProblems,
                show, exclude, include, move, shift, new Label { Text = "м", AutoSize = true, Margin = new Padding(0, 7, 3, 0) }, delete, count,
            });
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(350, 0),
                Text = "Номер и отметку можно исправить прямо в таблице (жёлтые столбцы), «Искл.» — временно не учитывать точку в треугольниках " +
                       "(точка станет серой). Щелчок по заголовку — сортировка, двойной щелчок по строке — показать точку на чертеже. " +
                       "Поиск — по номеру, отметке или источнику.",
                Padding = new Padding(3),
                ForeColor = SystemColors.GrayText,
            };
            Controls.Add(grid);
            Controls.Add(bar);
            Controls.Add(hint);
            Controls.Add(title);
            title.Dock = DockStyle.Top;
        }

        private void Decorate()
        {
            foreach (var hidden in new[] { "Key", "NumKey" }) grid.Columns[hidden].Visible = false;
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.ReadOnly = column.Name != "Отметка" && column.Name != "Номер" && column.Name != "Искл.";
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
            }
            grid.Columns["Отметка"].DefaultCellStyle.BackColor = Color.LightYellow;
            grid.Columns["Номер"].DefaultCellStyle.BackColor = Color.LightYellow;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (!string.IsNullOrEmpty(row.Cells["Ошибка"].Value as string)) row.DefaultCellStyle.ForeColor = Color.Firebrick;
                else if (row.Cells["Искл."].Value is bool excluded && excluded) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
        }

        private int Decimals => panel?.Snapshot.Settings.Decimals ?? KzmSettings.DefaultDecimals;

        private void FormatCell(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (!(e.Value is double value)) return;
            string name = grid.Columns[e.ColumnIndex].Name;
            e.Value = ElevationFormat.Format(value, name == "Отметка" ? Decimals : 3);
            e.FormattingApplied = true;
        }

        /// <summary>Both «145,32» and «145.32» are accepted whatever the Windows number settings are.</summary>
        private void ParseCell(object sender, DataGridViewCellParsingEventArgs e)
        {
            if (grid.Columns[e.ColumnIndex].Name != "Отметка") return;
            if (ElevationFormat.TryParse(Convert.ToString(e.Value), out double value))
            {
                e.Value = value;
                e.ParsingApplied = true;
            }
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
            title.Text = "Точки поверхности «" + surface.Info.Name + "»: " + surface.Points +
                         (surface.ExcludedPoints > 0 ? ", исключено " + surface.ExcludedPoints : string.Empty);
            points = DrawingData.ReadPoints(panel.Document, panel.Snapshot.Settings, id)
                .ToDictionary(point => point.Id.Handle.Value, point => point);
            Fill();
        }

        private void Fill()
        {
            filling = true;
            try
            {
                string sort = table.DefaultView.Sort;
                table.BeginLoadData();
                table.Rows.Clear();
                foreach (var point in points.Values)
                {
                    table.Rows.Add(point.Id.Handle.Value, NumberKey(point.Number), point.Number ?? string.Empty, point.Data.Excluded,
                        point.Position.X, point.Position.Y,
                        point.Elevation.IsValid ? (object)point.Elevation.Value.Value : DBNull.Value,
                        SourceKinds.Title(point.Data.Source) + (string.IsNullOrEmpty(point.Data.SourceHandle) ? string.Empty : " " + point.Data.SourceHandle),
                        XyModes.Title(point.Data.XyMode),
                        point.Elevation.Problem ?? string.Empty);
                }
                table.EndLoadData();
                table.DefaultView.Sort = string.IsNullOrEmpty(sort) ? "[NumKey] ASC, [Номер] ASC" : sort;
                ApplyFilter();
            }
            finally
            {
                filling = false;
            }
        }

        private void ApplyFilter()
        {
            var parts = new List<string>();
            if (onlyProblems.Checked) parts.Add("[Ошибка] <> ''");
            // Characters with a meaning in LIKE are dropped (numbers and sources never contain them).
            string text = new string(search.Text.Trim().Where(c => "[]*%".IndexOf(c) < 0).ToArray()).Replace("'", "''");
            if (text.Length > 0)
            {
                // The elevation is searched as typed: «145,3» finds 145,30…145,39.
                string number = text.Replace(',', '.');
                parts.Add("([Номер] LIKE '%" + text + "%' OR [Источник] LIKE '%" + text + "%' OR CONVERT([Отметка], 'System.String') LIKE '%" + number + "%')");
            }
            try { table.DefaultView.RowFilter = string.Join(" AND ", parts); }
            catch (EvaluateException) { table.DefaultView.RowFilter = string.Empty; }
            catch (SyntaxErrorException) { table.DefaultView.RowFilter = string.Empty; }
            Decorate();
        }

        private void SortBy(string column)
        {
            string key = "[" + (column == "Номер" ? "NumKey" : column) + "]";
            string current = table.DefaultView.Sort ?? string.Empty;
            bool ascending = !current.StartsWith(key + " ASC", StringComparison.Ordinal);
            table.DefaultView.Sort = key + (ascending ? " ASC" : " DESC") + (column == "Номер" ? ", [Номер]" + (ascending ? " ASC" : " DESC") : string.Empty);
            foreach (DataGridViewColumn c in grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
            grid.Columns[column].HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;
            Decorate();
        }

        private static long NumberKey(string number) =>
            long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : long.MaxValue;

        private SurfacePoint PointOf(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return null;
            object key = grid.Rows[rowIndex].Cells["Key"].Value;
            return key is long value && points.TryGetValue(value, out var point) ? point : null;
        }

        private void SavePoint(int rowIndex, string column)
        {
            var point = PointOf(rowIndex);
            if (point == null) return;
            var settings = panel.Snapshot.Settings;
            var id = point.Id;
            var cell = grid.Rows[rowIndex].Cells[column].Value;
            if (column == "Номер")
            {
                string number = Convert.ToString(cell)?.Trim() ?? string.Empty;
                if (number == (point.Number ?? string.Empty)) return;
                PanelActions.Run("номер точки", document =>
                {
                    EditServices.SetPoint(document.Database, id, null, number, settings);
                    PanelMessages.Info("Номер точки " + id.Handle + " = «" + number + "».");
                }, panel.RefreshAll);
            }
            else if (column == "Отметка")
            {
                if (!(cell is double value)) return;
                if (point.Elevation.IsValid && Math.Abs(point.Elevation.Value.Value - value) < 1e-9) return;
                PanelActions.Run("отметка точки", document =>
                {
                    EditServices.SetPoint(document.Database, id, value, null, settings);
                    PanelMessages.Info("Отметка точки " + (point.Number ?? id.Handle.ToString()) + " = " + ElevationFormat.Format(value, settings.Decimals) + ".");
                }, panel.RefreshAll);
            }
            else if (column == "Искл.")
            {
                bool excluded = cell is bool flag && flag;
                if (excluded == point.Data.Excluded) return;
                RunExclusion(new List<ObjectId> { id }, excluded);
            }
        }

        private List<SurfacePoint> Selected()
        {
            var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(row => PointOf(row.Index)).Where(point => point != null).ToList();
            if (selected.Count == 0 && grid.CurrentRow != null)
            {
                var current = PointOf(grid.CurrentRow.Index);
                if (current != null) selected.Add(current);
            }
            return selected;
        }

        private void ShowSelected()
        {
            var selected = Selected();
            if (selected.Count == 0) return;
            PanelActions.Show(panel.Document, selected.Select(point => point.Id),
                KzmCommands.ZoomSize(panel.Snapshot.Find(surfaceId)?.Info.Appearance.TextHeight));
        }

        private void SetExcluded(bool excluded)
        {
            var selected = Selected().Where(point => point.Data.Excluded != excluded).ToList();
            if (selected.Count == 0)
            {
                PanelMessages.Info(excluded ? "Выберите точки, которые нужно исключить." : "Среди выбранных нет исключённых точек.");
                return;
            }
            RunExclusion(selected.Select(point => point.Id).ToList(), excluded);
        }

        private void RunExclusion(List<ObjectId> ids, bool excluded)
        {
            var settings = panel.Snapshot.Settings;
            PanelActions.Run(excluded ? "исключить точки" : "вернуть точки", document =>
            {
                int changed = EditServices.SetExcluded(document.Database, ids, excluded, settings);
                PanelMessages.Info((excluded ? "Исключено из триангуляции точек: " : "Возвращено в триангуляцию точек: ") + changed + ".");
            }, panel.RefreshAll);
        }

        private void ShiftSelected()
        {
            var selected = Selected();
            if (selected.Count == 0) { PanelMessages.Info("Выберите точки."); return; }
            if (!ElevationFormat.TryParse(shift.Text, out double delta) || delta == 0)
            {
                PanelMessages.Error("введите сдвиг в метрах, например 0,10 или -0,05.");
                return;
            }
            if (MessageBox.Show(this, "Сдвинуть отметки выбранных точек (" + selected.Count + ") на " + ElevationFormat.FormatSigned(delta, 3) + " м?\nОтменить можно командой U.",
                    "КЗМ", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var ids = selected.Select(point => point.Id).ToList();
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("сдвиг отметок", document =>
            {
                int changed = EditServices.ShiftElevations(document.Database, ids, delta, settings);
                PanelMessages.Info("Отметки сдвинуты на " + ElevationFormat.FormatSigned(delta, 3) + " м у точек: " + changed + ".");
            }, panel.RefreshAll);
        }

        private void DeleteSelected()
        {
            var selected = Selected();
            if (selected.Count == 0) return;
            if (MessageBox.Show(this, "Удалить выбранные точки (" + selected.Count + ")?\nОтменить можно командой U.", "КЗМ",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var ids = selected.Select(point => point.Id).ToList();
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("удаление точек", document =>
            {
                int erased = EditServices.ErasePoints(document.Database, ids, settings);
                PanelMessages.Info("Удалено точек: " + erased + ".");
            }, panel.RefreshAll);
        }
    }
}
