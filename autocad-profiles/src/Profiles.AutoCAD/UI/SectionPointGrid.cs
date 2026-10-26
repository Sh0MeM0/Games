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

    public sealed class SectionAnchorElevationEventArgs : EventArgs
    {
        public double? Value { get; }
        public SectionAnchorElevationEventArgs(double? value) { Value = value; }
    }

    public sealed class SectionAnchorRequestedEventArgs : EventArgs
    {
        public int PointIndex { get; }
        public SectionAnchorRequestedEventArgs(int pointIndex) { PointIndex = pointIndex; }
    }

    public sealed class SectionElevationEditedEventArgs : EventArgs
    {
        public int PointIndex { get; }
        public SectionPointRole Role { get; }
        public bool Design { get; }
        public double? Value { get; }
        public SectionElevationEditedEventArgs(int pointIndex, SectionPointRole role, bool design, double? value)
        { PointIndex = pointIndex; Role = role; Design = design; Value = value; }
    }

    /// <summary>Horizontal section editor; rendered precision never changes the raw survey data.</summary>
    public sealed class SectionPointGrid : DataGridView
    {
        private const int VisiblePointCount = 13;
        private readonly List<SectionPoint> points = new List<SectionPoint>();
        private readonly Dictionary<string, string> invalid = new Dictionary<string, string>();
        private readonly ToolStripMenuItem interpolate;
        private readonly ToolStripMenuItem renamePoint;
        private readonly ToolStripMenuItem roleMenu;
        private readonly Dictionary<SectionPointRole, ToolStripMenuItem> roleItems = new Dictionary<SectionPointRole, ToolStripMenuItem>();
        private readonly System.ComponentModel.IContainer menuComponents = new System.ComponentModel.Container();
        private readonly ContextMenuStrip headerRoleMenu;
        private readonly Dictionary<SectionPointRole, ToolStripMenuItem> headerRoleItems = new Dictionary<SectionPointRole, ToolStripMenuItem>();
        private bool loading;
        private bool batchingChanges;
        private bool programmaticManual;
        private bool editorChanged;
        private bool editParsed;
        private int editingPoint = -1, editingRow = -1;
        private object editOriginalValue;
        private TextBox editingBox;
        private double? projectAnchorElevation;
        private string invalidAnchor;
        private readonly List<Action> pendingEditNotifications = new List<Action>();
        private bool manualDesignEnabled = true;
        private bool projectAnchorVisible = true;
        private bool fittingPointColumns;
        private int fittedPointWidth = -1;
        private int fittedPointMinimum = -1;
        private int fittedLabelWidth = -1;
        private int fittedColumnCount = -1;
        private Font anchorFont;

        public event EventHandler PointsChanged;
        public event EventHandler InterpolationRequested;
        public event EventHandler<SectionAnchorElevationEventArgs> ProjectAnchorElevationChanged;
        public event EventHandler<SectionAnchorRequestedEventArgs> ProjectAnchorRequested;
        public event EventHandler<SectionElevationEditedEventArgs> ElevationEdited;
        public int SelectedPointIndex => CurrentCell == null ? -1 : CurrentCell.ColumnIndex - 1;
        public SectionPointRow SelectedRowKind => (SectionPointRow)Math.Max(0, CurrentCell?.RowIndex ?? 0);
        public int? ProjectAnchorIndex { get; private set; }
        private int? projectDisplayIndex;
        public int? ProjectDisplayIndex => ProjectAnchorIndex ?? projectDisplayIndex;
        public bool ManualDesignEnabled
        {
            get => manualDesignEnabled;
            set { manualDesignEnabled = value; UpdateDesignMode(); }
        }
        public bool ProjectAnchorVisible
        {
            get => projectAnchorVisible;
            set { projectAnchorVisible = value; UpdateDesignMode(); }
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
            ColumnHeadersHeight = 66;
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
            DefaultCellStyle.Padding = new Padding(2, 0, 4, 0);
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 239, 244);
            ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Parameter", HeaderText = "Параметр", Frozen = true, ReadOnly = true,
                Width = 135, MinimumWidth = 125, SortMode = DataGridViewColumnSortMode.NotSortable
            });
            Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            Columns[0].DefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            Columns[0].DefaultCellStyle.Padding = new Padding(3, 0, 2, 0);
            Columns[0].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            Rows.Add("Интервал, м");
            Rows.Add("Факт, м");
            Rows.Add("Проект, м");
            Rows[0].Cells[0].ToolTipText = "Измеренное расстояние от предыдущей точки, м. У первой точки предыдущего интервала нет; неизвестные расстояния оставьте пустыми.";
            Rows[1].Cells[0].ToolTipText = "Фактическая отметка точки, м. Берега определяются по направлению течения.";
            Rows[2].Cells[0].ToolTipText = "Проектная отметка, м: ручные точки проекта либо отметка проектного дна в выбранном опорном столбце трапеции.";
            foreach (DataGridViewRow row in Rows) row.Height = 30;
            Rows[2].DefaultCellStyle.ForeColor = Color.Firebrick;

            ContextMenuStrip = new ContextMenuStrip();
            headerRoleMenu = new ContextMenuStrip();
            menuComponents.Add(ContextMenuStrip);
            menuComponents.Add(headerRoleMenu);
            interpolate = new ToolStripMenuItem("Интерполировать", null, (s, e) => RequestInterpolation());
            ContextMenuStrip.Items.Add(interpolate);
            ContextMenuStrip.Items.Add(new ToolStripSeparator());
            ContextMenuStrip.Items.Add("Добавить точку слева", null, (s, e) => RunAction(AddPointBefore));
            ContextMenuStrip.Items.Add("Добавить точку справа", null, (s, e) => RunAction(AddPointAfter));
            ContextMenuStrip.Items.Add("Удалить выбранную точку", null, (s, e) => RunAction(RemoveSelectedPoint));
            ContextMenuStrip.Items.Add(new ToolStripSeparator());
            renamePoint = new ToolStripMenuItem("Название точки…", null, (s, e) => RunAction(RenameSelectedPoint));
            ContextMenuStrip.Items.Add(renamePoint);
            roleMenu = new ToolStripMenuItem("Назначение точки");
            foreach (SectionPointRole role in new[] { SectionPointRole.LeftBank, SectionPointRole.Bottom, SectionPointRole.RightBank, SectionPointRole.Regular })
            {
                SectionPointRole assignedRole = role;
                var menuItem = new ToolStripMenuItem(RoleCaption(role), null, (s, e) => RunAction(() => AssignSelectedRole(assignedRole)));
                roleItems.Add(role, menuItem);
                roleMenu.DropDownItems.Add(menuItem);
                var headerItem = new ToolStripMenuItem(RoleCaption(role), null, (s, e) => RunAction(() => AssignSelectedRole(assignedRole)));
                headerRoleItems.Add(role, headerItem);
                headerRoleMenu.Items.Add(headerItem);
            }
            headerRoleMenu.Items.Add(new ToolStripSeparator());
            headerRoleMenu.Items.Add("Название точки…", null, (s, e) => RunAction(RenameSelectedPoint));
            ContextMenuStrip.Items.Add(roleMenu);
            ContextMenuStrip.Items.Add(new ToolStripSeparator());
            ContextMenuStrip.Items.Add("Вставить таблицу", null, (s, e) => RunAction(PasteTable));
            ContextMenuStrip.Opening += (s, e) =>
            {
                interpolate.Enabled = CanInterpolate();
                int selected = SelectedPointIndex;
                bool hasPoint = selected >= 0 && selected < points.Count;
                renamePoint.Enabled = roleMenu.Enabled = hasPoint;
                foreach (var entry in roleItems)
                {
                    int previous = entry.Key == SectionPointRole.Regular ? -1 : points.FindIndex(point => point.Role == entry.Key);
                    entry.Value.Checked = hasPoint && points[selected].Role == entry.Key;
                    entry.Value.Text = RoleCaption(entry.Key) +
                        (hasPoint && previous >= 0 && previous != selected ? " — перенести с № " + (previous + 1) : "");
                }
            };
            DataError += (s, e) => { e.ThrowException = false; e.Cancel = false; };
            FitThirteenPointColumns();
        }

        /// <summary>Reserves room for thirteen point columns without creating points or changing an active edit.</summary>
        public void FitThirteenPointColumns() => FitPointColumns(true);

        // Retained for existing form callers; all callers use the current layout.
        public void FitTwelvePointColumns() => FitThirteenPointColumns();
        public void FitElevenPointColumns() => FitThirteenPointColumns();
        public void FitTenPointColumns() => FitThirteenPointColumns();

        private void FitPointColumns(bool force)
        {
            if (fittingPointColumns || loading || IsDisposed || Columns.Count == 0 || ClientSize.Width <= 0) return;
            fittingPointColumns = true;
            int previousOffset = HorizontalScrollingOffset;
            SuspendLayout();
            try
            {
                // The interpolation arrow is drawn only in empty cells. Reserving
                // its width beside every number needlessly hides the last points.
                var numericPadding = new Padding(ScalePixels(2), 0, ScalePixels(4), 0);
                if (DefaultCellStyle.Padding != numericPadding) DefaultCellStyle.Padding = numericPadding;
                int borderWidth = BorderStyle == BorderStyle.None ? 0 : ScalePixels(BorderStyle == BorderStyle.Fixed3D ? 4 : 2);
                int scrollbarWidth = Controls.OfType<VScrollBar>().Where(bar => bar.Visible).Sum(bar => bar.Width);
                int contentWidth = ClientSize.Width - borderWidth - scrollbarWidth - (RowHeadersVisible ? RowHeadersWidth : 0);
                int minimum = Math.Max(ScalePixels(56), TextRenderer.MeasureText("-999.99", Font,
                    Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + DefaultCellStyle.Padding.Horizontal + ScalePixels(4));
                int minimumLabelWidth = Math.Max(ScalePixels(100), TextRenderer.MeasureText("Интервал, м", Font,
                    Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + ScalePixels(10));
                int labelWidth = Math.Max(minimumLabelWidth,
                    Math.Min(ScalePixels(135), contentWidth - VisiblePointCount * minimum));
                int available = contentWidth - labelWidth;
                // On smaller windows or with the extra earthworks panel open,
                // keep values readable and allow scrolling instead of shrinking text.
                int width = Math.Max(minimum, Math.Min(ScalePixels(120), Math.Max(0, available) / VisiblePointCount));
                if (!force && fittedPointWidth == width && fittedPointMinimum == minimum &&
                    fittedLabelWidth == labelWidth && fittedColumnCount == Columns.Count) return;

                Columns[0].MinimumWidth = minimumLabelWidth;
                if (Columns[0].Width != labelWidth) Columns[0].Width = labelWidth;
                for (int index = 1; index < Columns.Count; index++)
                {
                    Columns[index].MinimumWidth = minimum;
                    if (Columns[index].Width != width) Columns[index].Width = width;
                }
                // Width changes can adjust the scroll position. Retain the user's
                // horizontal offset; WinForms clamps it if the new range is shorter.
                if (HorizontalScrollingOffset != previousOffset) HorizontalScrollingOffset = previousOffset;
                fittedPointWidth = width;
                fittedPointMinimum = minimum;
                fittedLabelWidth = labelWidth;
                fittedColumnCount = Columns.Count;
            }
            finally
            {
                ResumeLayout(true);
                fittingPointColumns = false;
            }
        }

        private int ScalePixels(int logicalPixels) => Math.Max(1, (int)Math.Round(logicalPixels * DeviceDpi / 96.0));

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            FitPointColumns(false);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            FitPointColumns(false);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            FitPointColumns(false);
        }

        public void LoadPoints(IList<SectionPoint> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Count > 10000) throw new ProfileValidationException("В поперечнике не может быть больше 10000 точек.");
            if (source.Any(point => point == null)) throw new ProfileValidationException("Поперечник содержит отсутствующую точку.");
            points.Clear();
            points.AddRange(source.Select(Copy));
            invalid.Clear();
            invalidAnchor = null;
            ProjectAnchorIndex = null;
            projectDisplayIndex = null;
            projectAnchorElevation = null;
            RebuildColumns();
        }

        public List<SectionPoint> ReadPoints()
        {
            EndEdit();
            if (invalidAnchor != null)
                throw new ProfileValidationException("Проектное дно в опорном столбце: исправьте значение «" + invalidAnchor + "» в режиме «Трапеция».");
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
            UpdateDesignMode();
        }

        /// <summary>A scalar editor for an unanchored centre; never changes geometry or saved anchor identity.</summary>
        public void SetProjectDisplayIndex(int? index)
        {
            if (index.HasValue && (index.Value < 0 || index.Value >= points.Count))
                throw new ArgumentOutOfRangeException(nameof(index));
            projectDisplayIndex = index;
            UpdateDesignMode();
        }

        public void SetElevationAt(int index, bool design, double value)
        {
            if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ProfileValidationException("Отметка должна быть конечным числом.");
            int row = design ? 2 : 1;
            bool anchor = design && IsAnchor(index);
            if (anchor) { projectAnchorElevation = value; invalidAnchor = null; }
            else if (design) points[index].Design = value;
            else points[index].Existing = value;
            if (!anchor) invalid.Remove(Key(index, row));
            loading = true;
            Rows[row].Cells[index + 1].Value = value;
            Rows[row].Cells[index + 1].ErrorText = "";
            loading = false;
            NotifyElevationEdit(index, row);
            OnPointsChanged();
        }

        public void AddPointBefore() => InsertPoint(SelectedPointIndex < 0 ? 0 : SelectedPointIndex);
        public void AddPointAfter() => InsertPoint(SelectedPointIndex < 0 ? points.Count : SelectedPointIndex + 1);

        public void RenameSelectedPoint()
        {
            EndEdit();
            int index = SelectedPointIndex;
            if (index < 0 || index >= points.Count)
                throw new ProfileValidationException("Выберите столбец точки, которую нужно назвать.");
            using (var dialog = new Form
            {
                Text = "Название точки № " + (index + 1), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi, Font = Font,
                ClientSize = new Size(440, 180)
            })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var note = new Label
                {
                    Text = "До 100 символов. Пустое название возвращает подпись по назначению точки.",
                    AutoSize = true, MaximumSize = new Size(410, 0), Margin = new Padding(0, 0, 0, 8)
                };
                var input = new TextBox { Text = points[index].Label ?? "", MaxLength = 100, Dock = DockStyle.Top };
                var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(410, 0) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
                var save = new Button { Text = "Сохранить", AutoSize = true };
                save.Click += (s, e) =>
                {
                    if (input.Text.Length > 100 || input.Text.Any(char.IsControl))
                    {
                        error.Text = "Введите не более 100 символов без переносов строк и управляющих знаков.";
                        input.Focus();
                        return;
                    }
                    dialog.DialogResult = DialogResult.OK;
                };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(save);
                layout.Controls.Add(note, 0, 0);
                layout.Controls.Add(input, 0, 1);
                layout.Controls.Add(error, 0, 2);
                layout.Controls.Add(buttons, 0, 3);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => { input.Focus(); input.SelectAll(); };
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (index >= points.Count) throw new ProfileValidationException("Выбранная точка больше не существует.");
                string label = input.Text.Trim();
                if (points[index].Label == label) return;
                points[index].Label = label;
                UpdateHeaders();
                OnPointsChanged();
            }
        }

        public void AssignSelectedRole(SectionPointRole role)
        {
            if (!Enum.IsDefined(typeof(SectionPointRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
            ReadPoints(); // Preserve invalid draft text; resolve it before changing shared roles.
            int index = SelectedPointIndex;
            if (index < 0 || index >= points.Count)
                throw new ProfileValidationException("Выберите столбец, которому нужно назначить берег или дно.");
            bool changed = points[index].Role != role || points[index].UseLongitudinalElevation != (role != SectionPointRole.Regular);
            if (role != SectionPointRole.Regular)
                for (int previous = 0; previous < points.Count; previous++)
                    if (previous != index && points[previous].Role == role)
                    {
                        points[previous].Role = SectionPointRole.Regular;
                        points[previous].UseLongitudinalElevation = false;
                        changed = true;
                    }
            if (!changed) return;
            points[index].Role = role;
            points[index].UseLongitudinalElevation = role != SectionPointRole.Regular;
            UpdateHeaders();
            // A measured value explicitly assigned to a shared role writes through.
            // A blank role binds to the existing longitudinal value, never clears it.
            if (role != SectionPointRole.Regular && points[index].Existing.HasValue) NotifyElevationEdit(index, 1);
            OnPointsChanged();
        }

        private static string RoleCaption(SectionPointRole role) => role == SectionPointRole.LeftBank ? "Левый берег" :
            role == SectionPointRole.Bottom ? "Дно" : role == SectionPointRole.RightBank ? "Правый берег" : "Обычная точка";

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
            if (projectDisplayIndex >= index) projectDisplayIndex++;
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
            if (ProjectAnchorIndex == index) { ProjectAnchorIndex = null; invalidAnchor = null; }
            else if (ProjectAnchorIndex > index) ProjectAnchorIndex--;
            if (projectDisplayIndex == index) projectDisplayIndex = null;
            else if (projectDisplayIndex > index) projectDisplayIndex--;
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
                    Name = "Point" + index, ValueType = typeof(object), Width = 90, MinimumWidth = 60,
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
            FitThirteenPointColumns();
            Invalidate();
        }

        private void UpdateHeaders()
        {
            for (int index = 0; index < points.Count; index++)
            {
                string name = points[index].Role == SectionPointRole.LeftBank ? "Л. берег" :
                    points[index].Role == SectionPointRole.RightBank ? "П. берег" :
                    points[index].Role == SectionPointRole.Bottom ? "Дно" : "Точка";
                bool anchor = ProjectAnchorVisible && !ManualDesignEnabled && ProjectDisplayIndex == index;
                bool named = !string.IsNullOrWhiteSpace(points[index].Label);
                // Every column keeps its number, including a named bank or the
                // project anchor. The bottom of the header is a real, always
                // visible role-menu button rather than a hidden context action.
                Columns[index + 1].HeaderText = (named ? points[index].Label + "\n" + name : name + "\n") +
                    " № " + (index + 1) + (anchor ? " · П" : "");
                Columns[index + 1].HeaderCell.Style.Padding = new Padding(1, 0, 1, ScalePixels(23));
                Columns[index + 1].ToolTipText = (named ? points[index].Label + "\n" : "") + "Точка " + (index + 1) + ": " +
                    (points[index].Role == SectionPointRole.LeftBank ? "левый берег по направлению течения" :
                    points[index].Role == SectionPointRole.RightBank ? "правый берег по направлению течения" :
                    points[index].Role == SectionPointRole.Bottom ? "фактическое дно" : "точка поперечника") +
                    (anchor ? (ProjectAnchorIndex.HasValue ? ". Опорный столбец проектного дна трапеции." : ". Ячейка проектного дна; центр между точками сохраняется.") : ".") +
                    "\nКнопка «Назначить» — левый берег, дно, правый берег или обычная точка. Двойной щелчок — название.";
                Columns[index + 1].HeaderCell.Style.ForeColor = anchor ? Color.Firebrick : Color.Black;
            }
        }

        private void UpdateDesignMode()
        {
            if (Rows.Count < 3) return;
            bool wasLoading = loading;
            loading = true;
            try
            {
                for (int index = 0; index < points.Count; index++)
                {
                    var cell = Rows[2].Cells[index + 1];
                    bool anchor = IsAnchor(index);
                    cell.ReadOnly = !manualDesignEnabled && !anchor;
                    if (editingPoint == index && editingRow == 2 && IsCurrentCellInEditMode) continue;
                    string error;
                    if (anchor)
                    {
                        cell.Value = invalidAnchor ?? (object)projectAnchorElevation;
                        cell.ErrorText = invalidAnchor == null ? "" : "Исправьте проектную отметку дна.";
                    }
                    else if (manualDesignEnabled)
                    {
                        cell.Value = invalid.TryGetValue(Key(index, 2), out error) ? (object)error : points[index].Design;
                        cell.ErrorText = error == null ? "" : "Исправьте проектную отметку.";
                    }
                    else { cell.Value = null; cell.ErrorText = ""; }
                }
            }
            finally { loading = wasLoading; }
            UpdateHeaders();
            Invalidate();
        }

        private bool IsAnchor(int index) => !manualDesignEnabled && projectAnchorVisible && ProjectDisplayIndex == index;

        protected override void OnCellBeginEdit(DataGridViewCellCancelEventArgs e)
        {
            // The reused editor is initialized before EditingControlShowing. Do not
            // mistake that framework setup for a change made by the user.
            if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
            editingPoint = e.ColumnIndex - 1;
            editingRow = e.RowIndex;
            editOriginalValue = e.RowIndex == 2 && IsAnchor(editingPoint) ?
                invalidAnchor ?? (object)projectAnchorElevation : Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
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
            bool anchor = e.RowIndex == 2 && IsAnchor(index);
            bool valid = false;
            try
            {
                double? value = cell.Value is double number ? number :
                    NumberParser.ParseOptional(Convert.ToString(cell.Value, CultureInfo.InvariantCulture), RowName(e.RowIndex));
                if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
                    throw new ProfileValidationException("Значение должно быть конечным числом.");
                if (e.RowIndex == 0 && value.HasValue && (index == 0 ? value.Value != 0 : value.Value <= 0))
                    throw new ProfileValidationException(index == 0 ? "У первой точки нет предыдущего интервала." : "Расстояние должно быть положительным либо пустым.");
                if (anchor) { projectAnchorElevation = value; invalidAnchor = null; }
                else
                {
                    SetValue(points[index], e.RowIndex, value);
                    invalid.Remove(Key(index, e.RowIndex));
                }
                cell.ErrorText = "";
                loading = true;
                cell.Value = value;
                loading = false;
                valid = true;
            }
            catch (ProfileValidationException error)
            {
                string text = Convert.ToString(cell.Value, CultureInfo.InvariantCulture);
                if (anchor) invalidAnchor = text;
                else invalid[Key(index, e.RowIndex)] = text;
                cell.ErrorText = error.Message;
            }
            // Editing callbacks run only after the editor has finished committing.
            // This lets the parent synchronize a shared value without reentering EndEdit.
            if (!programmaticManual && editingPoint == index && editingRow == e.RowIndex) return;
            if (valid) NotifyElevationEdit(index, e.RowIndex);
            OnPointsChanged();
        }

        protected override void OnCellEndEdit(DataGridViewCellEventArgs e)
        {
            if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
            int index = editingPoint, row = editingRow;
            bool changed = editParsed && editorChanged && index >= 0 && index < points.Count;
            bool valid = changed && (row == 2 && IsAnchor(index) ? invalidAnchor == null : !invalid.ContainsKey(Key(index, row)));
            editingPoint = editingRow = -1;
            base.OnCellEndEdit(e);
            if (valid) NotifyElevationEdit(index, row);
            if (changed) OnPointsChanged();
        }

        protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
        {
            if (e.ColumnIndex > 0) e.CellStyle.Font = Font;
            if (e.ColumnIndex > 0 && e.RowIndex == 2 && !ManualDesignEnabled)
            {
                bool anchor = IsAnchor(e.ColumnIndex - 1);
                e.Value = anchor ? invalidAnchor ?? (projectAnchorElevation.HasValue ? projectAnchorElevation.Value.ToString("F2", CultureInfo.InvariantCulture) : "") : "";
                if (anchor && invalidAnchor == null)
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
            if (e.ColumnIndex > 0 && e.RowIndex == -1)
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
                var button = HeaderRoleButton(e.CellBounds);
                ControlPaint.DrawButton(e.Graphics, button, ButtonState.Normal);
                string caption = TextRenderer.MeasureText("Назначить ▾", Font,
                    Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + ScalePixels(4) <= button.Width
                    ? "Назначить ▾" : "Роль ▾";
                TextRenderer.DrawText(e.Graphics, caption, Font, button, Color.FromArgb(35, 65, 85),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            }
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && !Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly &&
                string.IsNullOrWhiteSpace(Convert.ToString(Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture)))
            {
                e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
                int x = e.CellBounds.Right - ScalePixels(9), y = e.CellBounds.Top + e.CellBounds.Height / 2;
                using (var brush = new SolidBrush(Color.DimGray))
                    e.Graphics.FillPolygon(brush, new[] { new Point(x - ScalePixels(3), y - ScalePixels(2)), new Point(x + ScalePixels(3), y - ScalePixels(2)), new Point(x, y + ScalePixels(3)) });
                e.Handled = true;
            }
            base.OnCellPainting(e);
        }

        protected override void OnCellMouseDown(DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex >= 0 && e.Button == MouseButtons.Right)
                CurrentCell = Rows[e.RowIndex < 0 ? 1 : e.RowIndex].Cells[e.ColumnIndex];
            base.OnCellMouseDown(e);
        }

        protected override void OnColumnHeaderMouseDoubleClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnColumnHeaderMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || e.ColumnIndex <= 0) return;
            if (e.Y >= ColumnHeadersHeight - ScalePixels(24)) return;
            CurrentCell = Rows[1].Cells[e.ColumnIndex];
            RunAction(RenameSelectedPoint);
        }

        protected override void OnCellDoubleClick(DataGridViewCellEventArgs e)
        {
            base.OnCellDoubleClick(e);
            if (e.RowIndex != 2 || e.ColumnIndex <= 0 || ManualDesignEnabled || IsAnchor(e.ColumnIndex - 1)) return;
            CurrentCell = Rows[2].Cells[e.ColumnIndex];
            ProjectAnchorRequested?.Invoke(this, new SectionAnchorRequestedEventArgs(e.ColumnIndex - 1));
        }

        private Rectangle HeaderRoleButton(Rectangle bounds) => new Rectangle(bounds.Left + ScalePixels(3),
            bounds.Bottom - ScalePixels(23), Math.Max(1, bounds.Width - ScalePixels(6)), ScalePixels(20));

        protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnColumnHeaderMouseClick(e);
            if (e.Button != MouseButtons.Left || e.ColumnIndex <= 0 || e.Y < ColumnHeadersHeight - ScalePixels(24)) return;
            RunAction(() =>
            {
                ReadPoints();
                CurrentCell = Rows[1].Cells[e.ColumnIndex];
                int selected = e.ColumnIndex - 1;
                foreach (var entry in headerRoleItems)
                {
                    int previous = entry.Key == SectionPointRole.Regular ? -1 : points.FindIndex(point => point.Role == entry.Key);
                    entry.Value.Text = RoleCaption(entry.Key) + (previous >= 0 && previous != selected ? " — перенести с № " + (previous + 1) : "");
                    entry.Value.Checked = points[selected].Role == entry.Key;
                }
                // WinForms still uses the popup after raising Closed. Keep it alive for
                // repeated clicks and dispose it with the grid, never inside Closed.
                var bounds = GetCellDisplayRectangle(e.ColumnIndex, -1, true);
                headerRoleMenu.Show(this, new Point(bounds.Left, bounds.Bottom));
            });
        }

        protected override void OnCellMouseClick(DataGridViewCellMouseEventArgs e)
        {
            base.OnCellMouseClick(e);
            if (e.Button == MouseButtons.Left && e.ColumnIndex > 0 && e.RowIndex >= 0 && e.X >= Columns[e.ColumnIndex].Width - ScalePixels(18))
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
            if (!ManualDesignEnabled && data.Length + startRow > 2)
            {
                var design = data[2 - startRow];
                for (int column = 0; column < design.Length; column++)
                    if (!IsAnchor(startPoint + column) && !string.IsNullOrWhiteSpace(design[column]))
                        throw new ProfileValidationException("В режиме «Трапеция» редактируется только проектное дно в опорном столбце. Для остальных проектных отметок выберите режим «По проектным отметкам».");
            }
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
                        if (targetRow == 2 && !ManualDesignEnabled && !IsAnchor(point)) continue;
                        if (targetRow == 0 && point == 0)
                        {
                            continue;
                        }
                        var cell = Rows[targetRow].Cells[point + 1];
                        bool unchanged = Equals(cell.Value, value);
                        cell.Value = value;
                        if (unchanged && !invalid.ContainsKey(Key(point, targetRow)) && (targetRow != 2 || !IsAnchor(point) || invalidAnchor == null))
                            NotifyElevationEdit(point, targetRow);
                    }
            }
            finally
            {
                programmaticManual = previousManual;
                batchingChanges = previousBatch;
                FlushEditNotifications();
                OnPointsChanged();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V)) { RunAction(PasteTable); return true; }
            if (keyData == Keys.Delete && !IsCurrentCellInEditMode)
            {
                bool previousBatch = batchingChanges;
                batchingChanges = true;
                programmaticManual = true;
                try
                {
                    foreach (DataGridViewCell cell in SelectedCells)
                        if (cell.ColumnIndex > 0 && !cell.ReadOnly)
                        {
                            bool alreadyEmpty = cell.Value == null;
                            cell.Value = null;
                            if (alreadyEmpty) NotifyElevationEdit(cell.ColumnIndex - 1, cell.RowIndex);
                        }
                }
                finally
                {
                    programmaticManual = false;
                    batchingChanges = previousBatch;
                    FlushEditNotifications();
                    OnPointsChanged();
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnPointsChanged() { if (!loading && !batchingChanges) PointsChanged?.Invoke(this, EventArgs.Empty); }
        private void NotifyElevationEdit(int index, int row)
        {
            if (row != 1 && row != 2) return;
            if (index < 0 || index >= points.Count) return;
            Action notification;
            if (row == 2 && IsAnchor(index))
            {
                var args = new SectionAnchorElevationEventArgs(projectAnchorElevation);
                notification = () => ProjectAnchorElevationChanged?.Invoke(this, args);
            }
            else
            {
                var args = new SectionElevationEditedEventArgs(index, points[index].Role, row == 2, GetValue(points[index], row));
                notification = () => ElevationEdited?.Invoke(this, args);
            }
            if (batchingChanges) pendingEditNotifications.Add(notification);
            else notification();
        }

        private void FlushEditNotifications()
        {
            if (batchingChanges || pendingEditNotifications.Count == 0) return;
            var notifications = pendingEditNotifications.ToArray();
            pendingEditNotifications.Clear();
            foreach (var notification in notifications) notification();
        }
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
            Label = point.Label,
            Existing = point.Existing, Design = point.Design, UseLongitudinalElevation = point.UseLongitudinalElevation
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (editingBox != null) editingBox.TextChanged -= EditorTextChanged;
                menuComponents.Dispose();
                anchorFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            anchorFont?.Dispose();
            anchorFont = null;
            base.OnFontChanged(e);
            FitPointColumns(false);
        }

        protected override void OnCellToolTipTextNeeded(DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.ColumnIndex > 0 && e.RowIndex >= 0 && e.ColumnIndex - 1 < points.Count)
            {
                int index = e.ColumnIndex - 1;
                if (e.RowIndex == 2 && IsAnchor(index))
                    e.ToolTipText = "Проектное дно" + (projectAnchorElevation.HasValue ? ": " + projectAnchorElevation.Value.ToString("G17", CultureInfo.InvariantCulture) + " м" : "") +
                        ". Можно ввести или изменить прямо в ячейке." + (ProjectAnchorIndex.HasValue ? "" : " Ячейка показывает общую отметку; заданный центр сохраняется, даже если находится между точками.");
                else if (e.RowIndex == 2 && !ManualDesignEnabled)
                    e.ToolTipText = "Двойной щелчок или кнопка «Дно под точкой» — разместить центр проектного дна под этой точкой.";
                else if (GetValue(points[index], e.RowIndex).HasValue)
                    e.ToolTipText = "Исходное значение: " + GetValue(points[index], e.RowIndex).Value.ToString("G17", CultureInfo.InvariantCulture) +
                        (e.RowIndex == 1 && points[index].UseLongitudinalElevation ? ". Общая отметка с продольным профилем." : "");
            }
            base.OnCellToolTipTextNeeded(e);
        }
    }
}
