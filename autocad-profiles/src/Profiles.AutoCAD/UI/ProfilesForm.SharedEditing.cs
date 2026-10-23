using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private bool synchronizingUi;
        private bool sharedCommitQueued;
        private List<LongitudinalPoint> sharedLongitudinalBaseline;
        private CrossSection sectionBeforeEditing;
        private CrossSection requestedSectionSelection;
        private sealed class PendingSharedValue
        {
            internal double? Value;
            internal long Order;
        }
        private readonly Dictionary<Tuple<double, SharedProfileField>, PendingSharedValue> pendingSharedValues = new Dictionary<Tuple<double, SharedProfileField>, PendingSharedValue>();
        private readonly Dictionary<DataGridViewCell, long> pendingLongitudinalCells = new Dictionary<DataGridViewCell, long>();
        private long sharedEditOrder;

        private bool HasActiveNumericEditor => longGrid.IsCurrentCellInEditMode || crossGrid.IsCurrentCellInEditMode ||
            new[] { horizontal, vertical, datum, metres, textHeight, textWidth, sectionWater, projectBottom, bottomWidth,
                leftSlope, rightSlope, centerOffset, siltThickness, siltLeft, siltRight, sectionName }.Any(input => input.ContainsFocus);

        private void InitializeSharedEditing()
        {
            foreach (var input in new[] { sectionWater, projectBottom, bottomWidth, leftSlope, rightSlope })
                input.Validated += (s, e) =>
                {
                    if (!loading && !synchronizingUi && input.Tag is NumberState state && state.Edited)
                    {
                        try
                        {
                            var field = input == sectionWater ? SharedProfileField.Water : input == projectBottom ? SharedProfileField.ProjectBottom :
                                input == bottomWidth ? SharedProfileField.BottomWidth : input == leftSlope ? SharedProfileField.LeftSlope : SharedProfileField.RightSlope;
                            double? value = ReadOptionalNumber(input, field.ToString());
                            if (input == projectBottom)
                            {
                                ActivateProjectBottom(value, !state.Value.HasValue);
                                DisplayNumber(projectBottom, value);
                            }
                            if (currentSectionStation.HasValue) RecordSharedValue(currentSectionStation.Value, field, value);
                        }
                        catch (ProfileValidationException ex) { SetStatus(ex.Message, true); }
                    }
                    QueueSharedCommit();
                };
            centerOffset.Validated += (s, e) =>
            {
                if (loading || synchronizingUi) return;
                try
                {
                    UpdateProjectDisplayIndex(ReadCurrentSection(), false);
                    RefreshProjectAnchorDisplay();
                    QueueSharedCommit();
                }
                catch (ProfileValidationException ex) { SetStatus(ex.Message, true); }
            };
            longGrid.CellEndEdit += (s, e) =>
            {
                if (!loading && longEditChanged && longEditParsed)
                    RecordLongitudinalCell(longGrid.Rows[e.RowIndex].Cells[e.ColumnIndex]);
                QueueSharedCommit();
            };
            crossGrid.CellEndEdit += (s, e) => QueueSharedCommit();
            crossGrid.ProjectAnchorElevationChanged += (s, e) =>
            {
                if (loading || synchronizingUi) return;
                DisplayNumber(projectBottom, e.Value);
                ActivateProjectBottom(e.Value, sectionBeforeEditing == null || !sectionBeforeEditing.ProjectBottom.HasValue);
                if (currentSectionStation.HasValue) RecordSharedValue(currentSectionStation.Value, SharedProfileField.ProjectBottom, e.Value);
                SetDirty();
                QueueSharedCommit();
            };
            crossGrid.ElevationEdited += (s, e) =>
            {
                if (loading || synchronizingUi) return;
                if (e.Design && SelectedDesignMode == SectionDesignMode.Points && crossGrid.ProjectAnchorIndex == e.PointIndex)
                {
                    DisplayNumber(projectBottom, e.Value);
                    if (currentSectionStation.HasValue) RecordSharedValue(currentSectionStation.Value, SharedProfileField.ProjectBottom, e.Value);
                }
                if (!e.Design && currentSectionStation.HasValue)
                {
                    SharedProfileField? field = e.Role == SectionPointRole.LeftBank ? SharedProfileField.LeftBank :
                        e.Role == SectionPointRole.RightBank ? SharedProfileField.RightBank :
                        e.Role == SectionPointRole.Bottom ? SharedProfileField.ExistingBottom : (SharedProfileField?)null;
                    if (field.HasValue) RecordSharedValue(currentSectionStation.Value, field.Value, e.Value);
                }
                QueueSharedCommit();
            };
        }

        private void RecordSharedValue(double station, SharedProfileField field, double? value)
        {
            pendingSharedValues[Tuple.Create(station, field)] = new PendingSharedValue { Value = value, Order = ++sharedEditOrder };
        }

        private void RecordLongitudinalCell(DataGridViewCell cell)
        {
            if (loading || synchronizingUi || cell.OwningRow.IsNewRow) return;
            if (!Enum.TryParse(cell.OwningColumn.Name, out SharedProfileField field)) return;
            try
            {
                Required(cell.OwningRow, "Station");
                Optional(cell.OwningRow, cell.OwningColumn.Name);
                pendingLongitudinalCells[cell] = ++sharedEditOrder;
            }
            catch (ProfileValidationException) { /* The original incomplete cell stays visible and blocks commit. */ }
        }

        private void QueueSharedCommit()
        {
            if (loading || synchronizingUi || sharedCommitQueued || !IsHandleCreated || IsDisposed || Disposing) return;
            sharedCommitQueued = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    sharedCommitQueued = false;
                    if (IsDisposed || Disposing || loading || synchronizingUi || HasActiveNumericEditor || stationInputEdited) return;
                    try
                    {
                        CommitSection();
                        RecalculateDerivedValues();
                        RefreshEarthworks();
                    }
                    catch (Exception ex) { SetStatus("Изменения сохранены в редакторе: " + ex.Message, true); }
                }));
            }
            catch (InvalidOperationException) { sharedCommitQueued = false; }
        }

        private ProfileProject SynchronizationSource(List<LongitudinalPoint> longitudinal)
        {
            var source = CloneProfileContainer();
            source.ProjectId = currentProjectId ?? Guid.NewGuid().ToString("N");
            source.Name = currentProjectName ?? "Проект";
            source.Kind = activeKind;
            source.Template = SelectedTemplate;
            source.Longitudinal = longitudinal;
            source.Sections = sections.Select(CloneSection).ToList();
            return source;
        }

        private void CommitSynchronizedSection()
        {
            if (synchronizingUi || selectedSection < 0 || selectedSection >= sections.Count) return;
            int index = selectedSection;
            var previous = sectionBeforeEditing == null ? CloneSection(sections[index]) : CloneSection(sectionBeforeEditing);
            var edited = ReadCurrentSection();
            var longitudinal = ReadLongitudinal();
            var source = SynchronizationSource(longitudinal);
            if (sharedLongitudinalBaseline != null)
                source = ProjectSynchronization.RefreshFromLongitudinal(source, sharedLongitudinalBaseline);
            var synchronized = ProjectSynchronization.CommitSection(source, index, previous, edited);
            var explicitChanges = pendingSharedValues.Select(change => new
            { Station = change.Key.Item1, Field = change.Key.Item2, change.Value.Value, change.Value.Order }).ToList();
            foreach (var cell in pendingLongitudinalCells)
            {
                if (cell.Key.DataGridView != longGrid || cell.Key.OwningRow.IsNewRow) continue;
                if (!Enum.TryParse(cell.Key.OwningColumn.Name, out SharedProfileField field)) continue;
                explicitChanges.Add(new { Station = Required(cell.Key.OwningRow, "Station"), Field = field,
                    Value = Optional(cell.Key.OwningRow, cell.Key.OwningColumn.Name), Order = cell.Value });
            }
            foreach (var change in explicitChanges.OrderBy(change => change.Order))
                synchronized = ProjectSynchronization.SetSharedValue(synchronized, change.Station, change.Field, change.Value);
            ApplySynchronizedProject(synchronized, index);
            pendingSharedValues.Clear();
            pendingLongitudinalCells.Clear();
        }

        private void ApplySynchronizedProject(ProfileProject project, int selection)
        {
            int requested = requestedSectionSelection == null ? -1 : sections.IndexOf(requestedSectionSelection);
            int point = crossGrid.SelectedPointIndex;
            int row = (int)crossGrid.SelectedRowKind;
            bool old = loading;
            synchronizingUi = true;
            loading = true;
            try
            {
                ApplyLongitudinalValues(project.Longitudinal);
                sections.Clear();
                sections.AddRange(project.Sections.Select(CloneSection));
                if (requested >= 0 && requested < sections.Count) requestedSectionSelection = sections[requested];
                selectedSection = selection;
                SortSections(selection >= 0 && selection < sections.Count ? sections[selection] : null);
                DisplaySection();
                if (point >= 0 && point + 1 < crossGrid.Columns.Count)
                    crossGrid.CurrentCell = crossGrid.Rows[row].Cells[point + 1];
                sharedLongitudinalBaseline = CopyLongitudinal(project.Longitudinal);
            }
            finally { loading = old; synchronizingUi = false; }
        }

        private void ApplyLongitudinalValues(IList<LongitudinalPoint> points)
        {
            var rows = longGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow && !RowEmpty(row))
                .ToDictionary(row => Required(row, "Station"));
            foreach (var point in points)
            {
                if (!rows.TryGetValue(point.Station, out var row))
                {
                    int index = longGrid.Rows.Add();
                    row = longGrid.Rows[index];
                }
                SetLongitudinalRow(row, point);
            }
            SortLongitudinalRows();
        }

        private static void SetLongitudinalRow(DataGridViewRow row, LongitudinalPoint point)
        {
            row.Cells["Station"].Value = point.Station;
            row.Cells["Ground"].Value = point.Ground;
            row.Cells["LeftBank"].Value = point.LeftBank;
            row.Cells["RightBank"].Value = point.RightBank;
            row.Cells["ExistingBottom"].Value = point.ExistingBottom;
            row.Cells["Water"].Value = point.Water;
            row.Cells["ProjectBottom"].Value = point.ProjectBottom;
            row.Cells["BottomWidth"].Value = point.BottomWidth;
            row.Cells["LeftSlope"].Value = point.LeftSlope;
            row.Cells["RightSlope"].Value = point.RightSlope;
            row.Cells["ProjectGround"].Value = point.ProjectGround;
            row.Cells["PipeInvert"].Value = point.PipeInvert;
            row.Cells["PipeDescription"].Value = point.PipeDescription;
            row.Cells["Foundation"].Value = point.Foundation;
            row.Cells["PointName"].Value = point.PointName;
            row.Cells["PeatDepth"].Value = point.PeatDepth;
        }

        private static List<LongitudinalPoint> CopyLongitudinal(IEnumerable<LongitudinalPoint> points)
        {
            return points.Select(point => new LongitudinalPoint
            {
                Station = point.Station, Ground = point.Ground, LeftBank = point.LeftBank, RightBank = point.RightBank,
                ExistingBottom = point.ExistingBottom, Water = point.Water, ProjectBottom = point.ProjectBottom,
                BottomWidth = point.BottomWidth, LeftSlope = point.LeftSlope, RightSlope = point.RightSlope,
                ProjectGround = point.ProjectGround, PipeInvert = point.PipeInvert, PipeDescription = point.PipeDescription,
                Foundation = point.Foundation, PointName = point.PointName, PeatDepth = point.PeatDepth
            }).ToList();
        }
    }
}
