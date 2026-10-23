using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD.UI
{
    public enum SectionPointRow { Distance = 0, Existing = 1, Design = 2 }

    /// <summary>Horizontal section editor; rendered precision never changes the raw survey data.</summary>
    public sealed class SectionPointGrid : DataGridView
    {
        private readonly List<SectionPoint> points = new List<SectionPoint>();
        private readonly Dictionary<string, string> invalid = new Dictionary<string, string>();
        private readonly ToolStripMenuItem interpolate;
        private bool loading;
        private bool batchingChanges;
        private bool programmaticManual;
        private bool editorChanged;
        private bool editParsed;
        private int editingPoint = -1, editingRow = -1;
        private object editOriginalValue;
        private TextBox editingBox;
        private double? projectAnchorElevation;
        private bool manualDesignEnabled = true;
        private bool projectAnchorVisible = true;
        private Font anchorFont;

        public event EventHandler PointsChanged;
        public event EventHandler InterpolationRequested;
        public int SelectedPointIndex => CurrentCell == null ? -1 : CurrentCell.ColumnIndex - 1;
        public SectionPointRow SelectedRowKind => (SectionPointRow)Math.Max(0, CurrentCell?.RowIndex ?? 0);
        public int? ProjectAnchorIndex { get; private set; }
        public bool ManualDesignEnabled
        {
            get => manualDesignEnabled;
            set { manualDesignEnabled = value; UpdateDesignMode(); }
        }
        public bool ProjectAnchorVisible
        {
            get => projectAnchorVisible;
            set { projectAnchorVisible = value; UpdateHeaders(); Invalidate(); }
        }
        public bool IsSelectedCellEmpty => SelectedPointIndex >= 0 &&
            string.IsNullOrWhiteSpace(Convert.ToString(CurrentCell.Value, CultureInfo.InvariantCulture));

        public SectionPointGrid()
        {
            Dock = DockStyle.Fill;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToOrderColumns = false;
            AllowUserToResizeRows = false;
            AutoGenerateColumns = false;
            RowHeadersVisible = false;
            ColumnHeadersHeight = 58;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            BackgroundColor = Color.White;
            SelectionMode = DataGridViewSelectionMode.CellSelect;
            MultiSelect = true;
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            DefaultCellStyle.Format = "F2";
            DefaultCellStyle.FormatProvider = CultureInfo.InvariantCulture;
            DefaultCellStyle.NullValue = "";
            DefaultCellStyle.Padding = new Padding(3, 0, 18, 0);
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 239, 244);
            ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Parameter", HeaderText = "Параметр", Frozen = true, ReadOnly = true,
                Width = 250, MinimumWidth = 190, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            Columns[0].DefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            Rows.Add("Расстояние от предыдущей точки, м");
            Rows.Add("Фактическая отметка, м");
            Rows.Add("Проектная отметка, м");
            foreach (DataGridViewRow row in Rows) row.Height = 38;
            Rows[2].DefaultCellStyle.ForeColor = Color.Firebrick;

            ContextMenuStrip = new ContextMenuStrip();
            interpolate = new ToolStripMenuItem("Интерполировать", null, (s, e) => RequestInterpolation());
            ContextMenuStrip.Items.Add(interpolate);
            ContextMenuStrip.Items.Add(new ToolStripSeparator());
            ContextMenuStrip.Items.Add("Добавить точку слева", null, (s, e) => RunAction(AddPointBefore));
            ContextMenuStrip.Items.Add("Добавить точку справа", null, (s, e) => RunAction(AddPointAfter));
            ContextMenuStrip.Items.Add("Удалить выбранную точку", null, (s, e) => RunAction(RemoveSelectedPoint));
            ContextMenuStrip.Items.Add(new ToolStripSeparator());
            ContextMenuStrip.Items.Add("Вставить таблицу", null, (s, e) => RunAction(PasteTable));
            ContextMenuStrip.Opening += (s, e) => interpolate.Enabled = CanInterpolate();
            DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
        }

        public void LoadPoints(IList<SectionPoint> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Count > 10000) throw new ProfileValidationException("В поперечнике не может быть больше 10000 точек.");
            if (source.Any(point => point == null)) throw new ProfileValidationException("Поперечник содержит отсутствующую точку.");
            points.Clear();
            points.AddRange(source.Select(Copy));
            invalid.Clear();
            ProjectAnchorIndex = null;
            projectAnchorElevation = null;
            RebuildColumns();
        }

        public List<SectionPoint> ReadPoints()
        {
            EndEdit();
            if (invalid.Count > 0)
            {
                string key = invalid.Keys.First();
                int[] at = key.Split(':').Select(int.Parse).ToArray();
                throw new ProfileValidationException("Точка " + (at[0] + 1) + ", " + RowName(at[1]) + ": исправьте значение «" + invalid[key] + "».");
            }
            return points.Select(Copy).ToList();
        }

        public void SetProjectAnchor(int? index)
        {
            SetProjectAnchor(index, projectAnchorElevation);
        }

        public void SetProjectAnchor(int? index, double? elevation)
        {
            if (index.HasValue && (index.Value < 0 || index.Value >= points.Count))
                throw new ArgumentOutOfRangeException(nameof(index), "Проектная опорная точка отсутствует в поперечнике.");
            ProjectAnchorIndex = index;
            projectAnchorElevation = elevation;
            UpdateHeaders();
            Invalidate();
        }

        public void SetElevationAt(int index, bool design, double value)
        {
            if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ProfileValidationException("Отметка должна быть конечным числом.");
            int row = design ? 2 : 1;
            if (design) points[index].Design = value;
            else { points[index].Existing = value; points[index].UseLongitudinalElevation = false; }
            invalid.Remove(Key(index, row));
            loading = true;
            Rows[row].Cells[index + 1].Value = value;
            Rows[row].Cells[index + 1].ErrorText = "";
            loading = false;
            OnPointsChanged();
        }

        public void AddPointBefore() => InsertPoint(SelectedPointIndex < 0 ? 0 : SelectedPointIndex);
        public void AddPointAfter() => InsertPoint(SelectedPointIndex < 0 ? points.Count : SelectedPointIndex + 1);

        private void InsertPoint(int index)
        {
            EndEdit();
            if (points.Count >= 10000) throw new ProfileValidationException("В поперечнике нельзя добавить больше 10000 точек.");
            ReindexInvalid(index, 1, false);
            points.Insert(index, new SectionPoint
            {
                // An inserted first point keeps the section's signed survey origin.
                Offset = index == 0 && points.Count > 0 ? points[0].Offset : 0,
                Role = SectionPointRole.Regular,
                DistanceFromPrevious = index == 0 ? 0 : (double?)null,
                UseLongitudinalElevation = false
            });
            // A split interval has no measured component lengths yet.
            if (index + 1 < points.Count)
            {
                points[index + 1].DistanceFromPrevious = null;
                invalid.Remove(Key(index + 1, 0));
            }
            if (ProjectAnchorIndex >= index) ProjectAnchorIndex++;
            RebuildColumns();
            CurrentCell = Rows[0].Cells[index + 1];
            OnPointsChanged();
        }

        public void RemoveSelectedPoint()
        {
            int index = SelectedPointIndex;
            if (index < 0 || index >= points.Count) return;
            EndEdit();
            double firstOrigin = points[0].Offset;
            if (index == 0 && points.Count > 1 && !invalid.ContainsKey(Key(1, 0)))
            {
                // With a measured interval the remaining points retain their absolute
                // coordinates. With an unknown interval retain the established origin.
                double? interval = points[1].DistanceFromPrevious;
                if (interval.HasValue && interval.Value > 0)
                {
                    double nextOrigin = firstOrigin + interval.Value;
                    if (!double.IsNaN(nextOrigin) && !double.IsInfinity(nextOrigin)) firstOrigin = nextOrigin;
                }
            }
            points.RemoveAt(index);
            ReindexInvalid(index, -1, true);
            if (ProjectAnchorIndex == index) ProjectAnchorIndex = null;
            else if (ProjectAnchorIndex > index) ProjectAnchorIndex--;
            // Do not silently add neighboring intervals or reuse a stale interval.
            if (index < points.Count)
            {
                if (index == 0) points[0].Offset = firstOrigin;
                points[index].DistanceFromPrevious = index == 0 ? 0 : (double?)null;
                invalid.Remove(Key(index, 0));
            }
            RebuildColumns();
            if (points.Count > 0) CurrentCell = Rows[1].Cells[Math.Min(index, points.Count - 1) + 1];
            OnPointsChanged();
        }

        private void ReindexInvalid(int index, int shift, bool removing)
        {
            var entries = invalid.ToList();
            invalid.Clear();
            foreach (var entry in entries)
            {
                int[] at = entry.Key.Split(':').Select(int.Parse).ToArray();
                if (removing && at[0] == index) continue;
                int point = at[0] >= index ? at[0] + shift : at[0];
                invalid[Key(point, at[1])] = entry.Value;
            }
        }

        private void RebuildColumns()
        {
            loading = true;
            editingPoint = editingRow = -1;
            CurrentCell = null;
            while (Columns.Count > 1) Columns.RemoveAt(Columns.Count - 1);
            for (int index = 0; index < points.Count; index++)
            {
                Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "Point" + index, ValueType = typeof(object), Width = 140, MinimumWidth = 100,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
                for (int row = 0; row < 3; row++)
                {
                    var cell = Rows[row].Cells[index + 1];
                    string error;
                    cell.Value = invalid.TryGetValue(Key(index, row), out error) ? (object)error : GetValue(points[index], row);
                    cell.ErrorText = error == null ? "" : "Некорректное число; исправьте или очистите ячейку.";
                    cell.ReadOnly = row == 0 && index == 0;
                }
            }
            UpdateHeaders();
            UpdateDesignMode();
            loading = false;
            Invalidate();
        }

        private void UpdateHeaders()
        {
            for (int index = 0; index < points.Count; index++)
            {
                string name = points[index].Role == SectionPointRole.LeftBank ? "Левый берег" :
                    points[index].Role == SectionPointRole.RightBank ? "Правый берег" :
                    points[index].Role == SectionPointRole.Bottom ? "Дно" : "Точка " + (index + 1);
                bool anchor = ProjectAnchorVisible && !ManualDesignEnabled && ProjectAnchorIndex == index;
                Columns[index + 1].HeaderText = name + (anchor ? "\n▼ Проектное дно" : "\n№ " + (index + 1));
                Columns[index + 1].HeaderCell.Style.ForeColor = anchor ? Color.Firebrick : Color.Black;
            }
        }

        private void UpdateDesignMode()
        {
            if (Rows.Count < 3) return;
            for (int index = 1; index < Columns.Count; index++) Rows[2].Cells[index].ReadOnly = !manualDesignEnabled;
            UpdateHeaders();
            Invalidate();
        }

        protected override void OnCellBeginEdit(DataGridViewCellCancelEventArgs e)
        {
            // The reused editor is initialized before EditingControlShowing. Do not
            // mistake that framework setup for a change made by the user.
            if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
            editingPoint = e.ColumnIndex - 1;
            editingRow = e.RowIndex;
            editOriginalValue = Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            editorChanged = false;
            editParsed = false;
            base.OnCellBeginEdit(e);
        }

        protected override void OnEditingControlShowing(DataGridViewEditingControlShowingEventArgs e)
        {
            if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
            editingBox = e.Control as TextBox;
            base.OnEditingControlShowing(e);
            editorChanged = false;
            if (editingBox != null) editingBox.TextChanged += EditorTextChanged;
        }

        private void EditorTextChanged(object sender, EventArgs e) => editorChanged = true;

        protected override void OnCellParsing(DataGridViewCellParsingEventArgs e)
        {
            editParsed = true;
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && editingPoint == e.ColumnIndex - 1 && editingRow == e.RowIndex && !editorChanged)
            {
                // The editor may contain a rounded F2 representation of a more precise
                // double. Merely opening and closing it must retain the original value.
                e.Value = editOriginalValue;
                e.ParsingApplied = true;
            }
            else if (e.ColumnIndex > 0 && e.RowIndex >= 0)
            {
                e.Value = Convert.ToString(e.Value, CultureInfo.InvariantCulture);
                e.ParsingApplied = true;
            }
            base.OnCellParsing(e);
        }

        protected override void OnCellValueChanged(DataGridViewCellEventArgs e)
        {
            base.OnCellValueChanged(e);
            if (loading || e.ColumnIndex <= 0 || e.RowIndex < 0 || e.ColumnIndex - 1 >= points.Count) return;
            int index = e.ColumnIndex - 1;
            var cell = Rows[e.RowIndex].Cells[e.ColumnIndex];
            try
            {
                double? value = cell.Value is double number ? number :
                    NumberParser.ParseOptional(Convert.ToString(cell.Value, CultureInfo.InvariantCulture), RowName(e.RowIndex));
                if (e.RowIndex == 0 && value.HasValue && (index == 0 ? value.Value != 0 : value.Value <= 0))
                    throw new ProfileValidationException(index == 0 ? "У первой точки нет предыдущего интервала." : "Расстояние должно быть положительным либо пустым.");
                SetValue(points[index], e.RowIndex, value);
                if (e.RowIndex == 1 && (programmaticManual || (editingPoint == index && editorChanged)))
                    points[index].UseLongitudinalElevation = false;
                invalid.Remove(Key(index, e.RowIndex));
                cell.ErrorText = "";
                loading = true;
                cell.Value = value;
                loading = false;
            }
            catch (ProfileValidationException error)
            {
                invalid[Key(index, e.RowIndex)] = Convert.ToString(cell.Value, CultureInfo.InvariantCulture);
                cell.ErrorText = error.Message;
            }
            OnPointsChanged();
        }

        protected override void OnCellEndEdit(DataGridViewCellEventArgs e)
        {
            if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
            if (editingPoint >= 0 && editingPoint < points.Count && editingRow == 1 && editParsed && editorChanged && points[editingPoint].UseLongitudinalElevation)
            {
                points[editingPoint].UseLongitudinalElevation = false;
                OnPointsChanged();
            }
            editingPoint = editingRow = -1;
            base.OnCellEndEdit(e);
        }

        protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
        {
            if (e.ColumnIndex > 0) e.CellStyle.Font = Font;
            if (e.ColumnIndex > 0 && e.RowIndex == 2 && !ManualDesignEnabled)
            {
                bool anchor = ProjectAnchorVisible && ProjectAnchorIndex == e.ColumnIndex - 1 && projectAnchorElevation.HasValue;
                e.Value = anchor ? projectAnchorElevation.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
                if (anchor)
                {
                    if (anchorFont == null) anchorFont = new Font(Font, FontStyle.Italic);
                    e.CellStyle.Font = anchorFont;
                }
                e.FormattingApplied = true;
            }
            else if (e.ColumnIndex > 0 && e.RowIndex == 0 && e.ColumnIndex == 1)
            { e.Value = "—"; e.FormattingApplied = true; }
            else if (e.ColumnIndex > 0 && e.Value is double value)
            { e.Value = value.ToString("F2", CultureInfo.InvariantCulture); e.FormattingApplied = true; }
            base.OnCellFormatting(e);
        }

        protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && !Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly &&
                string.IsNullOrWhiteSpace(Convert.ToString(Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture)))
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
                int x = e.CellBounds.Right - 11, y = e.CellBounds.Top + e.CellBounds.Height / 2;
                using (var brush = new SolidBrush(Color.DimGray))
                    e.Graphics.FillPolygon(brush, new[] { new Point(x - 4, y - 2), new Point(x + 4, y - 2), new Point(x, y + 3) });
                e.Handled = true;
            }
            base.OnCellPainting(e);
        }

        protected override void OnCellMouseDown(DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && e.Button == MouseButtons.Right) CurrentCell = Rows[e.RowIndex].Cells[e.ColumnIndex];
            base.OnCellMouseDown(e);
        }

        protected override void OnCellMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnCellMouseClick(e);
            if (e.Button == MouseButtons.Left && e.ColumnIndex > 0 && e.RowIndex >= 0 && e.X >= Columns[e.ColumnIndex].Width - 20)
            {
                CurrentCell = Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (CanInterpolate())
                {
                    var bounds = GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
                    ContextMenuStrip.Show(this, new Point(bounds.Right - 18, bounds.Bottom));
                }
            }
        }

        private bool CanInterpolate() => SelectedPointIndex >= 0 && !CurrentCell.ReadOnly && IsSelectedCellEmpty;
        private void RequestInterpolation() { if (CanInterpolate()) InterpolationRequested?.Invoke(this, EventArgs.Empty); }

        public void PasteTable()
        {
            if (!Clipboard.ContainsText()) return;
            string raw = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            if (raw.Length == 0) return;
            var data = raw.Split('\n').Select(line => line.Split('\t')).ToArray();
            int startPoint = Math.Max(0, SelectedPointIndex), startRow = CurrentCell?.RowIndex ?? 0;
            if (data.Length + startRow > 3) throw new ProfileValidationException("У поперечника три строки: расстояние, факт и проект. Вставьте горизонтальную таблицу без заголовков.");
            int width = data.Max(row => row.Length);
            if (startPoint + width > 10000) throw new ProfileValidationException("Слишком много точек: максимум 10000.");
            if (startRow == 0 && startPoint == 0)
            {
                string first = data[0][0].Trim();
                if (first != "" && first != "—" && NumberParser.ParseRequired(first, "Первый интервал") != 0)
                    throw new ProfileValidationException("Первый столбец не имеет предыдущего интервала: оставьте пусто или 0.");
            }
            if (!ManualDesignEnabled && data.Length + startRow > 2 && data[2 - startRow].Any(value => !string.IsNullOrWhiteSpace(value)))
                throw new ProfileValidationException("Для вставки проектных отметок выберите режим проекта «По точкам».");
            bool previousBatch = batchingChanges;
            bool previousManual = programmaticManual;
            batchingChanges = true;
            try
            {
                EndEdit();
                int originalCount = points.Count;
                while (points.Count < startPoint + width)
                    points.Add(new SectionPoint
                    {
                        Role = SectionPointRole.Regular,
                        DistanceFromPrevious = points.Count == 0 ? 0 : (double?)null,
                        UseLongitudinalElevation = false
                    });
                if (points.Count != originalCount)
                {
                    RebuildColumns();
                    CurrentCell = Rows[startRow].Cells[startPoint + 1];
                }
                programmaticManual = true;
                for (int row = 0; row < data.Length; row++)
                    for (int column = 0; column < data[row].Length; column++)
                    {
                        int targetRow = startRow + row, point = startPoint + column;
                        string value = data[row][column].Trim();
                        if (targetRow == 2 && !ManualDesignEnabled) continue;
                        if (targetRow == 0 && point == 0)
                        {
                            continue;
                        }
                        if (targetRow == 1) points[point].UseLongitudinalElevation = false;
                        Rows[targetRow].Cells[point + 1].Value = value;
                    }
            }
            finally
            {
                programmaticManual = previousManual;
                batchingChanges = previousBatch;
                OnPointsChanged();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V)) { RunAction(PasteTable); return true; }
            if (keyData == Keys.Delete && !IsCurrentCellInEditMode)
            {
                programmaticManual = true;
                try
                {
                    foreach (DataGridViewCell cell in SelectedCells)
                        if (cell.ColumnIndex > 0 && !cell.ReadOnly)
                        {
                            if (cell.RowIndex == 1) points[cell.ColumnIndex - 1].UseLongitudinalElevation = false;
                            cell.Value = null;
                        }
                }
                finally { programmaticManual = false; }
                OnPointsChanged();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnPointsChanged() { if (!loading && !batchingChanges) PointsChanged?.Invoke(this, EventArgs.Empty); }
        private void RunAction(Action action)
        {
            try { action(); }
            catch (Exception error) { MessageBox.Show(FindForm(), error.Message, "Проверьте данные", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private static string Key(int index, int row) => index + ":" + row;
        private static string RowName(int row) => row == 0 ? "расстояние от предыдущей точки" : row == 1 ? "фактическая отметка" : "проектная отметка";
        private static double? GetValue(SectionPoint point, int row) => row == 0 ? point.DistanceFromPrevious : row == 1 ? point.Existing : point.Design;
        private static void SetValue(SectionPoint point, int row, double? value)
        { if (row == 0) point.DistanceFromPrevious = value; else if (row == 1) point.Existing = value; else point.Design = value; }
        private static SectionPoint Copy(SectionPoint point) => new SectionPoint
        {
            Offset = point.Offset, DistanceFromPrevious = point.DistanceFromPrevious, Role = point.Role,
            Existing = point.Existing, Design = point.Design, UseLongitudinalElevation = point.UseLongitudinalElevation
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
                anchorFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            anchorFont?.Dispose();
            anchorFont = null;
            base.OnFontChanged(e);
        }

        protected override void OnCellToolTipTextNeeded(DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && e.ColumnIndex - 1 < points.Count)
            {
                int index = e.ColumnIndex - 1;
                if (e.RowIndex == 2 && !ManualDesignEnabled && ProjectAnchorVisible && ProjectAnchorIndex == index && projectAnchorElevation.HasValue)
                    e.ToolTipText = "Проектное дно: " + projectAnchorElevation.Value.ToString("G17", CultureInfo.InvariantCulture) + " м. Показана опорная отметка трапеции; строка ручного проекта не изменена.";
                else if (GetValue(points[index], e.RowIndex).HasValue)
                    e.ToolTipText = "Исходное значение: " + GetValue(points[index], e.RowIndex).Value.ToString("G17", CultureInfo.InvariantCulture) +
                        (e.RowIndex == 1 && points[index].UseLongitudinalElevation ? ". Связано с продольным профилем; ручное изменение отключает связь." : "");
            }
            base.OnCellToolTipTextNeeded(e);
        }
    }
}
