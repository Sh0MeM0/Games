using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private bool orderingRows;
        private bool rowOrderQueued;

        private sealed class StationRowComparer : IComparer
        {
            private readonly Dictionary<DataGridViewRow, int> original;
            private readonly Dictionary<DataGridViewRow, double?> stations;
            internal StationRowComparer(IList<DataGridViewRow> rows)
            {
                original = rows.ToDictionary(row => row, row => row.Index);
                stations = rows.ToDictionary(row => row, RowStation);
            }
            public int Compare(object first, object second)
            {
                var a = (DataGridViewRow)first;
                var b = (DataGridViewRow)second;
                if (ReferenceEquals(a, b)) return 0;
                // WinForms normally handles its new-row placeholder itself. Keep
                // this comparer safe if it is also supplied that row.
                if (a.IsNewRow != b.IsNewRow) return a.IsNewRow ? 1 : -1;
                double? x, y;
                if (!stations.TryGetValue(a, out x)) x = RowStation(a);
                if (!stations.TryGetValue(b, out y)) y = RowStation(b);
                if (x.HasValue != y.HasValue) return x.HasValue ? -1 : 1;
                int order = x.HasValue ? x.Value.CompareTo(y.Value) : 0;
                int firstIndex, secondIndex;
                if (!original.TryGetValue(a, out firstIndex)) firstIndex = a.Index;
                if (!original.TryGetValue(b, out secondIndex)) secondIndex = b.Index;
                return order == 0 ? firstIndex.CompareTo(secondIndex) : order;
            }
        }

        private static double? RowStation(DataGridViewRow row)
        {
            if (row.IsNewRow) return null;
            object value = row.Cells["Station"].Value;
            if (value is double number) return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
            try { return NumberParser.ParseOptional(Convert.ToString(value, CultureInfo.InvariantCulture), "ПК"); }
            catch (ProfileValidationException) { return null; }
        }

        private void InitializeRowOrdering()
        {
            longGrid.CellEndEdit += (s, e) =>
            {
                if (loading || orderingRows) return;
                QueueRowOrdering();
            };
        }

        private void QueueRowOrdering()
        {
            if (rowOrderQueued || loading || orderingRows || !IsHandleCreated || IsDisposed || Disposing) return;
            rowOrderQueued = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    rowOrderQueued = false;
                    if (IsDisposed || Disposing || loading || longGrid.IsCurrentCellInEditMode) return;
                    try { SortLongitudinalRows(); }
                    catch (Exception ex) { ShowError("Не удалось упорядочить строки: " + ex.Message); }
                }));
            }
            catch (InvalidOperationException ex)
            {
                rowOrderQueued = false;
                if (!IsDisposed && !Disposing && IsHandleCreated) ShowError("Не удалось запланировать сортировку: " + ex.Message);
            }
        }

        private void SortLongitudinalRows()
        {
            if (orderingRows || longGrid.IsCurrentCellInEditMode || longGrid.IsDisposed) return;
            var rows = longGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToList();
            if (rows.Count < 2) return;
            var comparer = new StationRowComparer(rows);
            var ordered = rows.ToList();
            ordered.Sort((a, b) => comparer.Compare(a, b));
            if (rows.SequenceEqual(ordered)) return;
            var currentRow = longGrid.CurrentCell?.OwningRow;
            int currentColumn = longGrid.CurrentCell?.ColumnIndex ?? 0;
            var selected = longGrid.SelectedCells.Cast<DataGridViewCell>().Select(cell => new { Row = cell.OwningRow, Column = cell.ColumnIndex }).ToList();
            bool old = loading;
            orderingRows = true;
            loading = true;
            try
            {
                longGrid.Sort(comparer);
                if (currentRow != null && currentRow.DataGridView == longGrid)
                    longGrid.CurrentCell = currentRow.Cells[currentColumn];
                longGrid.ClearSelection();
                foreach (var cell in selected)
                    if (cell.Row.DataGridView == longGrid) cell.Row.Cells[cell.Column].Selected = true;
                derivedValues.Clear();
                longGrid.Invalidate();
            }
            finally { loading = old; orderingRows = false; }
        }

        private void InsertLongitudinalRow(bool after)
        {
            try
            {
                if (!longGrid.EndEdit() || longGrid.IsCurrentCellInEditMode)
                    throw new ProfileValidationException("Завершите или отмените редактирование текущей ячейки перед вставкой строки.");
                int index = longGrid.CurrentCell?.RowIndex ?? 0;
                int maximumIndex = longGrid.NewRowIndex >= 0 ? longGrid.NewRowIndex : longGrid.Rows.Count;
                if (after && index < maximumIndex) index++;
                index = Math.Max(0, Math.Min(index, maximumIndex));
                int rowCount = longGrid.Rows.Count - (longGrid.NewRowIndex >= 0 ? 1 : 0);
                if (rowCount >= 10000) throw new ProfileValidationException("Максимум 10 000 пикетов.");
                longGrid.Rows.Insert(index, new object[longGrid.Columns.Count]);
                longGrid.CurrentCell = longGrid.Rows[index].Cells["Station"];
                longGrid.Focus();
                longGrid.BeginEdit(true);
                SetDirty();
                SetStatus("Введите ПК: 6+50 означает 650 м. После завершения ввода строка займёт своё место по пикетажу.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void SortSections(CrossSection selected = null)
        {
            if (selected == null && selectedSection >= 0 && selectedSection < sections.Count) selected = sections[selectedSection];
            var sorted = sections.Select((section, index) => new { Section = section, Index = index })
                .OrderBy(item => item.Section.Station.HasValue ? 0 : 1)
                .ThenBy(item => item.Section.Station ?? 0).ThenBy(item => item.Index).Select(item => item.Section).ToList();
            sections.Clear();
            sections.AddRange(sorted);
            selectedSection = selected == null ? -1 : sections.FindIndex(section => ReferenceEquals(section, selected));
            RefreshSectionList(selectedSection);
        }

        private void InsertSection(bool after)
        {
            try
            {
                CommitStationInput();
                CommitSection();
                if (sections.Count >= 200) throw new ProfileValidationException("Максимум 200 поперечников в проекте.");
                int index = selectedSection < 0 ? sections.Count : selectedSection + (after ? 1 : 0);
                sections.Insert(index, NewSection(sections.Count + 1));
                selectedSection = index;
                RefreshSectionList(index);
                DisplaySection();
                sectionStation.Focus();
                SetDirty();
                SetStatus("Введите ПК нового поперечника. После назначения пикета список упорядочится численно; черновики без ПК остаются в конце.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }
}
