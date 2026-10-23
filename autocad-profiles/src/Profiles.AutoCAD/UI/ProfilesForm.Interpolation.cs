using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private readonly TextBox seedLeft = Input();
        private readonly TextBox seedBottom = Input();
        private readonly TextBox seedRight = Input();
        private readonly Timer interpolationTimer = new Timer { Interval = 180 };
        private readonly Dictionary<string, double> derivedValues = new Dictionary<string, double>();
        private Font interpolationFont;
        private string stationBeforeSelection;
        private readonly string[] elevationColumns = { "Ground", "LeftBank", "RightBank", "ExistingBottom", "Water", "ProjectBottom", "ProjectGround", "PipeInvert" };

        private void InitializeInterpolation()
        {
            interpolationFont = new Font(longGrid.Font, FontStyle.Italic);
            interpolationTimer.Tick += (s, e) =>
            {
                interpolationTimer.Stop();
                if (!loading && !longGrid.IsCurrentCellInEditMode) RecalculateDerivedValues();
            };
            longGrid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                e.CellStyle.Font = longGrid.Font;
                e.CellStyle.BackColor = e.RowIndex % 2 == 0 ? Color.White : longGrid.AlternatingRowsDefaultCellStyle.BackColor;
                string name = longGrid.Columns[e.ColumnIndex].Name;
                if (derivedValues.TryGetValue(e.RowIndex + ":" + name, out var value) &&
                    string.IsNullOrWhiteSpace(Convert.ToString(longGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture)))
                {
                    e.Value = value.ToString("F2", CultureInfo.InvariantCulture);
                    e.CellStyle.Font = interpolationFont;
                    e.CellStyle.BackColor = Color.FromArgb(241, 247, 253);
                    e.FormattingApplied = true;
                }
            };
            longGrid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string name = longGrid.Columns[e.ColumnIndex].Name;
                if (derivedValues.TryGetValue(e.RowIndex + ":" + name, out var value))
                    e.ToolTipText = "Расчётная отметка " + Format(value) + " м: линейная интерполяция между известными пикетами. Исходная ячейка остаётся пустой. Введите число, чтобы задать свою опорную отметку.";
            };
            sectionStation.DropDown += (s, e) =>
            {
                stationBeforeSelection = sectionStation.Text;
                RefreshStationList();
            };
            sectionStation.TextUpdate += (s, e) => stationBeforeSelection = sectionStation.Text;
            sectionStation.SelectionChangeCommitted += (s, e) => SelectStation();
            sectionWater.ForeColor = Color.RoyalBlue;
            projectBottom.ForeColor = Color.Firebrick;
            var interpolate = new ToolStripMenuItem("Интерполировать отметку", null, (s, e) => InterpolateCrossCell());
            crossGrid.ContextMenuStrip.Items.Insert(0, interpolate);
            crossGrid.ContextMenuStrip.Items.Insert(1, new ToolStripSeparator());
            crossGrid.ContextMenuStrip.Opening += (s, e) =>
            {
                var cell = crossGrid.CurrentCell;
                interpolate.Enabled = cell != null && !cell.OwningRow.IsNewRow &&
                    (cell.OwningColumn.Name == "Existing" || cell.OwningColumn.Name == "Design") &&
                    string.IsNullOrWhiteSpace(Convert.ToString(cell.Value, CultureInfo.InvariantCulture));
            };
            crossGrid.CellMouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
                    crossGrid.CurrentCell = crossGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            };
        }

        private static double? Elevation(LongitudinalPoint point, string field)
        {
            switch (field)
            {
                case "Ground": return point.Ground;
                case "LeftBank": return point.LeftBank;
                case "RightBank": return point.RightBank;
                case "ExistingBottom": return point.ExistingBottom;
                case "Water": return point.Water;
                case "ProjectBottom": return point.ProjectBottom;
                case "ProjectGround": return point.ProjectGround;
                case "PipeInvert": return point.PipeInvert;
                default: return null;
            }
        }

        private void RecalculateDerivedValues()
        {
            derivedValues.Clear();
            try
            {
                var raw = ReadLongitudinal();
                var resolved = ProfileInterpolation.ResolveLongitudinal(raw);
                int index = 0;
                foreach (DataGridViewRow row in longGrid.Rows)
                {
                    if (row.IsNewRow || RowEmpty(row)) continue;
                    foreach (string field in elevationColumns)
                    {
                        double? value = Elevation(resolved[index], field);
                        if (!Elevation(raw[index], field).HasValue && value.HasValue)
                            derivedValues[row.Index + ":" + field] = value.Value;
                    }
                    index++;
                }
                RefreshStationList(raw);
            }
            catch (ProfileValidationException)
            {
                // An incomplete number while typing must not display stale calculated values.
            }
            finally { longGrid.Invalidate(); }
        }

        private void RefreshStationList(List<LongitudinalPoint> points = null)
        {
            string text = sectionStation.Text;
            bool old = loading;
            loading = true;
            try
            {
                sectionStation.Items.Clear();
                var values = points ?? ReadLongitudinal();
                foreach (var point in values) sectionStation.Items.Add(Format(point.Station));
                sectionStation.Text = text;
            }
            catch (ProfileValidationException) { sectionStation.Text = text; }
            finally { loading = old; }
        }

        private Control BuildSeedPanel()
        {
            var group = new GroupBox { Text = "Опорные отметки выбранного ПК, м", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(7, 18, 7, 5) };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            var values = (FlowLayoutPanel)Fields("Левый берег", seedLeft, "Дно факт.", seedBottom, "Правый берег", seedRight);
            values.Controls.Add(Button("В таблицу точек", AddSeedPoints));
            panel.Controls.Add(values, 0, 0);
            panel.Controls.Add(Note("Отметки можно перенести в таблицу; реальные смещения берегов и дна нужно ввести самостоятельно."), 0, 1);
            group.Controls.Add(panel);
            return group;
        }

        private void SelectStation()
        {
            if (loading) return;
            try
            {
                double station = NumberParser.ParseRequired(sectionStation.Text, "Выбранный пикет");
                // Selection changed the station control already; preserve the old section's identity.
                string previousStation = stationBeforeSelection ?? (selectedSection >= 0 ? Format(sections[selectedSection].Station) : "");
                bool old = loading; loading = true; sectionStation.Text = previousStation; loading = old;
                CommitSection();
                int existing = sections.FindIndex(s => s.Station.HasValue && Math.Abs(s.Station.Value - station) < 1e-8);
                if (existing >= 0)
                {
                    selectedSection = existing;
                    RefreshSectionList(existing); DisplaySection();
                    SetStatus("Открыт существующий поперечник ПК " + Format(station) + ". Для обновления отметок нажмите «Обновить по ПК».");
                    return;
                }
                var seed = ProfileInterpolation.CreateSection(ReadLongitudinal(), station);
                bool reusable = selectedSection >= 0 && IsEmptySection(sections[selectedSection]);
                if (reusable) sections[selectedSection] = seed;
                else { sections.Add(seed); selectedSection = sections.Count - 1; }
                RefreshSectionList(selectedSection); DisplaySection(); SetDirty();
                SetStatus("Подтянуты отметки ПК " + Format(station) + ". Смещения не определены: внесите фактическую съёмку поперечника.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private static bool IsEmptySection(CrossSection section)
        {
            return section.Points.Count == 0 && !section.Station.HasValue && !section.LeftBank.HasValue &&
                !section.RightBank.HasValue && !section.ExistingBottom.HasValue && !section.Water.HasValue &&
                !section.ProjectBottom.HasValue && !section.BottomWidth.HasValue && !section.LeftSlope.HasValue && !section.RightSlope.HasValue;
        }

        private void RefreshStationSeed()
        {
            try
            {
                double station = NumberParser.ParseRequired(sectionStation.Text, "ПК поперечника");
                var seed = ProfileInterpolation.CreateSection(ReadLongitudinal(), station);
                if (MessageBox.Show(this,
                    "Обновить опорные отметки, воду, проектное дно и размеры из продольного профиля?\n\nСмещения, натурные и проектные отметки в таблице точек не изменятся. Их ручные изменения сохранятся.",
                    "Обновление по пикету", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                bool old = loading; loading = true;
                seedLeft.Text = Format(seed.LeftBank); seedBottom.Text = Format(seed.ExistingBottom); seedRight.Text = Format(seed.RightBank);
                sectionWater.Text = Format(seed.Water); projectBottom.Text = Format(seed.ProjectBottom);
                bottomWidth.Text = Format(seed.BottomWidth); leftSlope.Text = Format(seed.LeftSlope); rightSlope.Text = Format(seed.RightSlope);
                loading = old;
                SetDirty();
                SetStatus("Отметки и проектные параметры обновлены по ПК. Таблица поперечной съёмки сохранена без изменений.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void AddSeedPoints()
        {
            try
            {
                var values = new[]
                {
                    NumberParser.ParseOptional(seedLeft.Text, "Левый берег по ПК"),
                    NumberParser.ParseOptional(seedBottom.Text, "Дно по ПК"),
                    NumberParser.ParseOptional(seedRight.Text, "Правый берег по ПК")
                };
                if (!values.Any(v => v.HasValue)) throw new ProfileValidationException("Нет опорных отметок. Выберите пикет продольного профиля или заполните отметки выше.");
                if (crossGrid.Rows.Cast<DataGridViewRow>().Any(r => !r.IsNewRow && !RowEmpty(r)) &&
                    MessageBox.Show(this, "Добавить опорные отметки после имеющихся строк? Их смещения потребуется ввести и расположить строки слева направо.",
                        "Добавление точек", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                int first = crossGrid.Rows.Count - 1;
                foreach (double? value in values) if (value.HasValue) crossGrid.Rows.Add("", Format(value), "");
                crossGrid.CurrentCell = crossGrid.Rows[first].Cells[0];
                crossGrid.Focus();
                SetDirty();
                SetStatus("Введите реальные смещения добавленных точек. До этого новые строки не могут быть сохранены или построены.", true);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void FindAutoCenter()
        {
            try
            {
                var section = ReadCurrentSection();
                centerOffset.Text = Format(ProfileInterpolation.FindCenterOffset(section.Points));
                SetStatus("Центр найден по минимальной натурной отметке; для плоского дна — середина его участка. Проектное дно, ширину и откосы задайте отдельно.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void InterpolateCrossCell()
        {
            try
            {
                var cell = crossGrid.CurrentCell;
                if (cell == null || cell.OwningRow.IsNewRow ||
                    (cell.OwningColumn.Name != "Existing" && cell.OwningColumn.Name != "Design"))
                    throw new ProfileValidationException("Выберите пустую ячейку натурной или проектной отметки.");
                if (!string.IsNullOrWhiteSpace(Convert.ToString(cell.Value, CultureInfo.InvariantCulture)))
                    throw new ProfileValidationException("Ячейка уже заполнена. Очистите её, если хотите заменить отметку расчётной.");
                bool design = cell.OwningColumn.Name == "Design";
                double offset = Required(cell.OwningRow, "Offset");
                var points = ReadCurrentSection().Points;
                double? value = ProfileInterpolation.SectionElevationAt(points, offset, design);
                if (!value.HasValue) throw new ProfileValidationException("Для интерполяции нужны известные отметки слева и справа от выбранного смещения. Экстраполяция не выполняется.");
                cell.Value = Format(value.Value);
                SetDirty();
                SetStatus("Отметка " + Format(value.Value) + " м вычислена между ближайшими известными смещениями и записана в выбранную ячейку.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }
}
