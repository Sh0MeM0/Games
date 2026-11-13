using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Новая структурная линия (задание п. 1, 2.3): таблица вершин выбранной полилинии с отметками.
    /// Отметки берутся из 3D-полилинии и из точек поверхности в вершинах; остальные вводятся,
    /// заполняются по длине между известными или задаются началом и концом.
    /// </summary>
    internal sealed class LineView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        private readonly Label source = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly TextBox name = new TextBox { Width = 200 };
        private readonly CheckBox closed = new CheckBox { Text = "Замкнутая", AutoSize = true };
        private readonly TextBox start = new TextBox { Width = 70 };
        private readonly TextBox end = new TextBox { Width = 70 };
        private readonly DataGridView grid = new DataGridView();
        private readonly Label note = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private KzmPanel panel;
        private LineDraft draft;
        private bool filling;

        internal LineView()
        {
            var top = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4) };
            top.Controls.Add(title);
            top.Controls.Add(source);
            top.Controls.Add(Flow(new Label { Text = "Имя", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, name, closed));
            var pick = new Button { Text = "Другая полилиния…", AutoSize = true };
            pick.Click += (s, e) => PickPolyline(panel, draft?.SurfaceId);
            var gaps = new Button { Text = "Заполнить пустые по длине", AutoSize = true };
            gaps.Click += (s, e) => FillGaps();
            top.Controls.Add(Flow(pick, gaps));
            var interpolate = new Button { Text = "Все по длине", AutoSize = true };
            interpolate.Click += (s, e) => InterpolateAll();
            top.Controls.Add(Flow(new Label { Text = "Начало", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, start,
                new Label { Text = "конец", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, end, interpolate));
            top.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText,
                Text = "Отметку можно ввести в каждой вершине (жёлтый столбец) или задать начало и конец — промежуточные вершины получат " +
                       "отметки по длине линии. Вершины, где уже стоит точка поверхности, используют её (её отметка изменится, если её исправить).",
            });

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            foreach (var (key, header) in new[] { ("n", "№"), ("x", "X"), ("y", "Y"), ("s", "От начала, м"), ("z", "Отметка"), ("o", "Откуда") })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = header, ReadOnly = key != "z", SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns["z"].DefaultCellStyle.BackColor = Color.LightYellow;
            grid.CellValidating += (s, e) =>
            {
                if (filling || grid.Columns[e.ColumnIndex].Name != "z") return;
                string text = Convert.ToString(e.FormattedValue).Trim();
                if (text.Length > 0 && !ElevationFormat.TryParse(text, out _))
                {
                    PanelMessages.Error("введите отметку числом, например 145,32.");
                    e.Cancel = true;
                }
            };
            grid.CellEndEdit += (s, e) => TakeCell(e.RowIndex);

            var create = new Button { Text = "Создать структурную линию", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
            create.Click += (s, e) => Create();
            var cancel = new Button { Text = "Отмена", AutoSize = true };
            cancel.Click += (s, e) => panel?.ShowSurface(draft?.SurfaceId);
            var bottom = Flow(create, cancel);
            bottom.Dock = DockStyle.Bottom;
            note.Dock = DockStyle.Bottom;
            Controls.Add(grid);
            Controls.Add(note);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(350, 0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        internal void Edit(LineDraft value)
        {
            draft = value;
            if (draft == null) return;
            name.Text = draft.Name;
            closed.Checked = draft.Closed;
            start.Text = draft.Vertices.First().Elevation.HasValue ? SettingsView.FormatValue(Math.Round(draft.Vertices.First().Elevation.Value, 3)) : string.Empty;
            end.Text = draft.Vertices.Last().Elevation.HasValue ? SettingsView.FormatValue(Math.Round(draft.Vertices.Last().Elevation.Value, 3)) : string.Empty;
        }

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            var surface = draft == null ? null : panel.Snapshot.Find(draft.SurfaceId);
            title.Text = "Новая структурная линия" + (surface != null ? " — «" + surface.Info.Name + "»" : string.Empty);
            source.Text = draft?.SourceDescription ?? "Полилиния не выбрана.";
            Fill();
        }

        private void Fill()
        {
            filling = true;
            try
            {
                grid.Rows.Clear();
                if (draft == null) return;
                int decimals = panel?.Snapshot.Settings.Decimals ?? KzmSettings.DefaultDecimals;
                for (int i = 0; i < draft.Vertices.Count; i++)
                {
                    var v = draft.Vertices[i];
                    string origin = v.Origin;
                    if (v.ExistingHandle != null)
                    {
                        string number = "т." + (string.IsNullOrEmpty(v.ExistingNumber) ? v.ExistingHandle : v.ExistingNumber);
                        bool changes = v.ExistingElevation.HasValue && v.Elevation.HasValue && Math.Abs(v.ExistingElevation.Value - v.Elevation.Value) > 0.0005;
                        origin = (string.IsNullOrEmpty(origin) || origin == "точка поверхности" ? "точка поверхности " + number : origin + "; " + number) +
                                 (changes ? " (была " + ElevationFormat.Format(v.ExistingElevation.Value, decimals) + " — изменится)" : string.Empty);
                    }
                    if (v.OnArc) origin = (origin.Length > 0 ? origin + "; " : string.Empty) + "точка дуги";
                    int row = grid.Rows.Add((i + 1).ToString(CultureInfo.InvariantCulture), ElevationFormat.Format(v.Position.X, 3), ElevationFormat.Format(v.Position.Y, 3),
                        ElevationFormat.Format(v.Station, 2), v.Elevation.HasValue ? ElevationFormat.Format(v.Elevation.Value, Math.Max(decimals, 2)) : string.Empty, origin);
                    if (!v.Elevation.HasValue) grid.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick;
                }
                int empty = draft.Vertices.Count(v => !v.Elevation.HasValue);
                int existing = draft.Vertices.Count(v => v.ExistingHandle != null);
                note.Text = "Вершин: " + draft.Vertices.Count + "; уже есть точек поверхности: " + existing + "; будет создано: " + (draft.Vertices.Count - existing) +
                            (empty > 0 ? ".\nБез отметки: " + empty + " (красные) — введите или заполните." : ".");
                note.ForeColor = empty > 0 ? Color.Firebrick : SystemColors.ControlText;
            }
            finally
            {
                filling = false;
            }
        }

        private void TakeCell(int row)
        {
            if (filling || draft == null || row < 0 || row >= draft.Vertices.Count) return;
            string text = Convert.ToString(grid.Rows[row].Cells["z"].Value)?.Trim() ?? string.Empty;
            var vertex = draft.Vertices[row];
            if (text.Length == 0) { vertex.Elevation = null; vertex.Origin = string.Empty; }
            else if (ElevationFormat.TryParse(text, out double z)) { vertex.Elevation = z; vertex.Origin = "введено"; }
            BeginInvoke((Action)Fill);
        }

        private void FillGaps()
        {
            if (draft == null) return;
            int filled = SurfaceTools.FillGaps(draft);
            Fill();
            PanelMessages.Info(filled > 0 ? "Заполнено вершин по длине: " + filled + "." : "Нечего заполнять: нужны известные отметки по обе стороны от пустых вершин.");
        }

        private void InterpolateAll()
        {
            if (draft == null) return;
            if (!ElevationFormat.TryParse(start.Text, out double a) || !ElevationFormat.TryParse(end.Text, out double b))
            {
                PanelMessages.Error("введите отметки начала и конца линии.");
                return;
            }
            SurfaceTools.Interpolate(draft, a, b);
            Fill();
            PanelMessages.Info("Отметки всех вершин заданы по длине между началом и концом.");
        }

        private void Create()
        {
            if (draft == null || panel == null) return;
            if (grid.IsCurrentCellInEditMode) grid.EndEdit();
            draft.Name = name.Text.Trim();
            draft.Closed = closed.Checked && draft.Vertices.Count > 2;
            if (draft.Vertices.Any(v => !v.Elevation.HasValue))
            {
                PanelMessages.Error("не у всех вершин есть отметка (красные строки).");
                return;
            }
            var value = draft;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("структурная линия", document =>
            {
                var (created, changed) = SurfaceTools.CreateLine(document.Database, value, settings);
                PanelMessages.Info("Структурная линия «" + value.Name + "» создана: новых точек " + created +
                                   (changed > 0 ? ", изменено отметок точек " + changed : string.Empty) + ". Отменить — U.");
            }, () =>
            {
                draft = null;
                panel.AfterSurfaceChange(value.SurfaceId);
            });
        }

        /// <summary>«Добавить по полилинии…»: the user points at a polyline, the panel shows its vertices.</summary>
        internal static void PickPolyline(KzmPanel panel, string surfaceId)
        {
            if (panel == null || surfaceId == null) return;
            LineDraft picked = null;
            var settings = panel.Snapshot.Settings;
            PanelActions.Pick("структурная линия", document =>
            {
                var options = new Autodesk.AutoCAD.EditorInput.PromptEntityOptions("\nУкажите полилинию (2D/3D) или отрезок — структурную линию: ");
                options.SetRejectMessage("\nНужна полилиния или отрезок.");
                options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
                options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline2d), true);
                options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline3d), true);
                options.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Line), true);
                var pick = document.Editor.GetEntity(options);
                if (pick.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var entity = (Autodesk.AutoCAD.DatabaseServices.Entity)transaction.GetObject(pick.ObjectId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    picked = SurfaceTools.ReadPolyline(document.Database, transaction, entity, surfaceId, settings);
                    transaction.Commit();
                }
            }, () =>
            {
                if (picked == null) return;
                panel.ShowLineDraft(picked);
                PanelMessages.Info("Вершин: " + picked.Vertices.Count + ". Проверьте отметки и нажмите «Создать структурную линию».");
            });
        }
    }
}
