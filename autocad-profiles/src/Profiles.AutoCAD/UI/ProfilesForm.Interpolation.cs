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
        private readonly Timer interpolationTimer = new Timer { Interval = 180 };
        private readonly Dictionary<string, double> derivedValues = new Dictionary<string, double>();
        private readonly string[] elevationColumns = { "Ground", "LeftBank", "RightBank", "ExistingBottom", "Water", "ProjectBottom", "ProjectGround", "PipeInvert", "BottomWidth", "LeftSlope", "RightSlope" };
        private Font interpolationFont;
        private double? currentSectionStation;
        private bool stationInputEdited;
        private bool stationChangeActive;

        private sealed class StationChoice
        {
            internal double Value;
            public override string ToString() => StationCaption(Value);
        }

        private void InitializeInterpolation()
        {
            interpolationFont = new Font(longGrid.Font, FontStyle.Italic);
            interpolationTimer.Tick += (s, e) =>
            {
                interpolationTimer.Stop();
                if (loading || HasActiveNumericEditor || stationInputEdited) return;
                RecalculateDerivedValues();
                RefreshEarthworks();
            };
            longGrid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                e.CellStyle.Font = longGrid.Font;
                e.CellStyle.BackColor = e.RowIndex % 2 == 0 ? Color.White : longGrid.AlternatingRowsDefaultCellStyle.BackColor;
                string name = longGrid.Columns[e.ColumnIndex].Name;
                if (name == "PipeDescription" || name == "Foundation" || name == "PointName") return;
                var raw = longGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
                if (string.IsNullOrWhiteSpace(Convert.ToString(raw, CultureInfo.InvariantCulture)) && derivedValues.TryGetValue(e.RowIndex + ":" + name, out var calculated))
                {
                    e.Value = calculated.ToString("F2", CultureInfo.InvariantCulture);
                    e.CellStyle.Font = interpolationFont;
                    e.CellStyle.BackColor = Color.FromArgb(241, 247, 253);
                    e.FormattingApplied = true;
                }
                else
                {
                    try
                    {
                        double? value = raw is double number ? number : NumberParser.ParseOptional(Convert.ToString(raw, CultureInfo.InvariantCulture), name);
                        if (value.HasValue) { e.Value = value.Value.ToString("F2", CultureInfo.InvariantCulture); e.FormattingApplied = true; }
                    }
                    catch (ProfileValidationException) { }
                }
            };
            longGrid.CellToolTipTextNeeded += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                string name = longGrid.Columns[e.ColumnIndex].Name;
                if (derivedValues.TryGetValue(e.RowIndex + ":" + name, out var value))
                    e.ToolTipText = "Расчётное значение " + Format(value) + ": линейная интерполяция между известными пикетами. Исходная ячейка остаётся пустой. Введите число, чтобы задать своё опорное значение.";
            };
            sectionStation.DropDown += (s, e) => RefreshStationList();
            sectionStation.TextUpdate += (s, e) => { if (!loading) stationInputEdited = true; };
            sectionStation.SelectionChangeCommitted += (s, e) =>
            {
                if (loading || !(sectionStation.SelectedItem is StationChoice choice)) return;
                try { SwitchToStation(choice.Value); }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            sectionStation.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                try { CommitStationInput(); }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            sectionStation.Validated += (s, e) =>
            {
                if (loading) return;
                try { CommitStationInput(); }
                catch (Exception ex) { SetStatus("Пикет не применён: " + ex.Message, true); }
            };
            sectionWater.ForeColor = Color.RoyalBlue;
            projectBottom.ForeColor = Color.Firebrick;
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
                case "BottomWidth": return point.BottomWidth;
                case "LeftSlope": return point.LeftSlope;
                case "RightSlope": return point.RightSlope;
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
            catch (ProfileValidationException) { /* A partial input must not show stale derived values. */ }
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
                foreach (var point in points ?? ReadLongitudinal())
                    sectionStation.Items.Add(new StationChoice { Value = point.Station });
                sectionStation.Text = text;
            }
            catch (ProfileValidationException) { sectionStation.Text = text; }
            finally { loading = old; }
        }

        private static string StationCaption(double value)
        {
            double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            if (rounded < 0) return "ПК " + rounded.ToString("F2", CultureInfo.InvariantCulture);
            double picket = Math.Floor(rounded / 100);
            return "ПК " + picket.ToString("0", CultureInfo.InvariantCulture) + "+" + (rounded - picket * 100).ToString("00.00", CultureInfo.InvariantCulture);
        }

        private static string SectionCaption(CrossSection section) => section.Station.HasValue ? StationCaption(section.Station.Value) : string.IsNullOrWhiteSpace(section.Name) ? "Без названия" : section.Name;
        private double? ReadSectionStation() => currentSectionStation;

        private void CommitStationInput()
        {
            if (loading || stationChangeActive) return;
            CommitSectionName();
            if (!stationInputEdited) return;
            if (!string.IsNullOrWhiteSpace(sectionStation.Text))
            {
                string text = sectionStation.Text.Trim();
                if (text.StartsWith("ПК", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2).Trim();
                SwitchToStation(NumberParser.ParseRequired(text, "Пикет поперечника"));
                return;
            }
            // Validate the entire current draft before changing any linkage.
            var current = ReadCurrentSection();
            var points = current.Points;
            foreach (var point in points) point.UseLongitudinalElevation = false;
            int? anchor = crossGrid.ProjectAnchorIndex;
            crossGrid.LoadPoints(points);
            crossGrid.SetProjectAnchor(anchor, current.ProjectBottom);
            UpdateProjectDisplayIndex(current, !current.ProjectBottom.HasValue);
            currentSectionStation = null;
            longitudinalProjectBottomLinked = longitudinalWaterLinked = false;
            stationInputEdited = false;
            sectionName.ReadOnly = false;
            sectionName.Text = "Поперечник " + (selectedSection + 1);
            CommitSection();
            RefreshSectionList(selectedSection);
            SetDirty();
        }

        private void SwitchToStation(double station)
        {
            if (loading || stationChangeActive) return;
            string requestedText = sectionStation.Text;
            stationChangeActive = true;
            try
            {
                if (station < -1000000000 || station > 1000000000)
                    throw new ProfileValidationException("Пикет поперечника должен находиться в пределах от −1 000 000 000 до 1 000 000 000 м.");
                // Commit the old section under its cached station, not the just-typed value.
                CommitSection();
                stationInputEdited = false;
                int existing = sections.FindIndex(s => s.Station.HasValue && s.Station.Value == station);
                if (existing >= 0)
                {
                    selectedSection = existing;
                    RefreshSectionList(existing);
                    DisplaySection();
                    SetDirty();
                    SetStatus("Открыт существующий поперечник " + StationCaption(station) + ". Предыдущий поперечник сохранён в списке; ручные точки не заменены.");
                    return;
                }
                if (selectedSection >= 0 && !sections[selectedSection].Station.HasValue && !IsEmptySection(sections[selectedSection]))
                {
                    // An unsited imported section or copy must be assignable to a
                    // station without silently replacing its measured geometry.
                    var previous = CloneSection(sections[selectedSection]);
                    var assigned = CloneSection(previous);
                    assigned.Station = station;
                    assigned.Name = StationCaption(station);
                    var synchronized = ProjectSynchronization.CommitSection(SynchronizationSource(ReadLongitudinal()), selectedSection, previous, assigned);
                    ApplySynchronizedProject(synchronized, selectedSection);
                    SetDirty();
                    SetStatus("Поперечнику назначен " + StationCaption(station) + ". Его расстояния, отметки и проектные параметры сохранены.");
                    return;
                }
                // Assign the station before sampling its linked fields. This
                // creates a raw station row even when every mark is still blank,
                // without promoting interpolated values into manual anchors.
                var source = SynchronizationSource(ReadLongitudinal());
                int target = selectedSection;
                CrossSection blank;
                if (target >= 0 && IsEmptySection(source.Sections[target])) blank = CloneSection(source.Sections[target]);
                else
                {
                    blank = NewSection(source.Sections.Count + 1);
                    source.Sections.Add(blank);
                    target = source.Sections.Count - 1;
                }
                var seed = CloneSection(blank);
                if (seed.Points.Count == 0) seed.Points = NewSection(1).Points;
                seed.Station = station;
                seed.Name = StationCaption(station);
                seed.ProjectAnchorIndex = seed.Points.Count >= 2 ? 1 : (int?)null;
                seed.DesignMode = SectionDesignMode.Automatic;
                var seeded = ProjectSynchronization.CommitSection(source, target, blank, seed);
                ApplySynchronizedProject(seeded, target);
                SetDirty();
                SetStatus("Создан " + StationCaption(station) + ": берега, дно и вода связаны с продольным профилем. Введите измеренные расстояния между точками.");
            }
            catch
            {
                bool old = loading; loading = true; sectionStation.Text = requestedText; loading = old;
                stationInputEdited = true;
                throw;
            }
            finally { stationChangeActive = false; }
        }

        private static bool IsEmptySection(CrossSection section)
        {
            return !section.Station.HasValue && !section.LeftBank.HasValue && !section.RightBank.HasValue &&
                !section.ExistingBottom.HasValue && !section.Water.HasValue && !section.ProjectBottom.HasValue &&
                !section.BottomWidth.HasValue && !section.LeftSlope.HasValue && !section.RightSlope.HasValue &&
                !section.SiltThickness.HasValue && !section.SiltLeft.HasValue && !section.SiltRight.HasValue &&
                section.CenterOffset == 0 && !section.ProjectAnchorIndex.HasValue &&
                (section.Points.Count == 0 || (section.Points.Count == 3 &&
                    section.Points[0].Role == SectionPointRole.LeftBank && section.Points[1].Role == SectionPointRole.Bottom &&
                    section.Points[2].Role == SectionPointRole.RightBank)) &&
                section.Points.All(p => string.IsNullOrWhiteSpace(p.Label) && p.Offset == 0 && !p.Existing.HasValue &&
                    !p.Design.HasValue && (!p.DistanceFromPrevious.HasValue || p.DistanceFromPrevious == 0));
        }

        private void RefreshStationSeed()
        {
            try
            {
                CommitStationInput();
                CommitSection();
                if (!currentSectionStation.HasValue) throw new ProfileValidationException("Сначала выберите или введите пикет поперечника.");
                var sample = ProfileInterpolation.SampleLongitudinal(ReadLongitudinal(), currentSectionStation.Value);
                if (MessageBox.Show(this, "Обновить связанные отметки и заменить воду, проектное дно, ширину и откосы значениями продольного профиля?\n\nРасстояния и вручную изменённые фактические или проектные отметки точек сохранятся.",
                    "Обновление по пикету", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var source = SynchronizationSource(ReadLongitudinal());
                var section = source.Sections[selectedSection];
                section.UseLongitudinalWater = section.UseLongitudinalProjectBottom = true;
                section.BottomWidth = sample.BottomWidth;
                section.LeftSlope = sample.LeftSlope;
                section.RightSlope = sample.RightSlope;
                // This button adopts derived values; it is not an explicit edit
                // of a raw longitudinal anchor.
                ApplySynchronizedProject(ProjectSynchronization.RefreshFromLongitudinal(source), selectedSection);
                SetDirty();
                SetStatus("Связанные отметки и параметры обновлены по пикету. Введённая съёмка сохранена.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void RefreshLinkedSectionValues(List<LongitudinalPoint> longitudinal = null)
        {
            if (loading || stationInputEdited || !currentSectionStation.HasValue || crossGrid.IsCurrentCellInEditMode) return;
            try
            {
                var points = crossGrid.ReadPoints();
                bool anyLink = longitudinalProjectBottomLinked || longitudinalWaterLinked || points.Any(p => p.UseLongitudinalElevation);
                if (!anyLink) return;
                var sample = ProfileInterpolation.SampleLongitudinal(longitudinal ?? ReadLongitudinal(), currentSectionStation.Value);
                bool changed = false, pointsChanged = false;
                foreach (var point in points.Where(p => p.UseLongitudinalElevation))
                {
                    double? value = point.Role == SectionPointRole.LeftBank ? sample.LeftBank :
                        point.Role == SectionPointRole.Bottom ? sample.ExistingBottom :
                        point.Role == SectionPointRole.RightBank ? sample.RightBank : point.Existing;
                    if (point.Existing == value) continue;
                    point.Existing = value;
                    changed = pointsChanged = true;
                }
                if (longitudinalWaterLinked && ReadOptionalNumber(sectionWater, "Урез воды") != sample.Water)
                { DisplayNumber(sectionWater, sample.Water); changed = true; }
                if (longitudinalProjectBottomLinked && ReadOptionalNumber(projectBottom, "Проектное дно") != sample.ProjectBottom)
                {
                    bool firstProject = !ReadOptionalNumber(projectBottom, "Проектное дно").HasValue;
                    DisplayNumber(projectBottom, sample.ProjectBottom);
                    if (sample.ProjectBottom.HasValue && (automaticDesignMode || firstProject))
                        ActivateProjectBottom(sample.ProjectBottom, firstProject);
                    changed = true;
                }
                if (pointsChanged)
                {
                    int index = crossGrid.SelectedPointIndex, row = (int)crossGrid.SelectedRowKind;
                    int? anchor = crossGrid.ProjectAnchorIndex;
                    // Invalid manual text must remain in its textbox, without
                    // losing the existing point anchor when other links refresh.
                    double? anchorElevation = null;
                    try { anchorElevation = ReadOptionalNumber(projectBottom, "Проектное дно"); }
                    catch (ProfileValidationException) { }
                    crossGrid.LoadPoints(points);
                    crossGrid.SetProjectAnchor(anchor, anchorElevation);
                    UpdateProjectDisplayIndex(ReadCurrentSection(), !anchorElevation.HasValue);
                    if (index >= 0 && index < points.Count) crossGrid.CurrentCell = crossGrid.Rows[row].Cells[index + 1];
                }
                RefreshProjectAnchorDisplay();
                if (changed) SetDirty();
            }
            catch (ProfileValidationException ex) { SetStatus("Связанные отметки пока не обновлены: " + ex.Message, true); }
        }

        private void FindAutoCenter()
        {
            try
            {
                var section = ReadCurrentSection();
                // Keep the exact centre of a flat bottom. When it is between
                // survey points, only the editor cell uses a nearby column.
                var resolved = ProfileInterpolation.ResolveSection(section, ReadLongitudinal());
                ProfileInterpolation.FindCenterOffset(resolved.Points); // Explain incomplete survey data.
                section.ProjectAnchorIndex = null;
                ProfileInterpolation.EnsureProjectAnchor(section);
                crossGrid.SetProjectAnchor(section.ProjectAnchorIndex, section.ProjectBottom);
                UpdateProjectDisplayIndex(section, false);
                DisplayNumber(centerOffset, section.CenterOffset);
                SelectDesignMode(SectionDesignMode.Trapezoid);
                SetDirty();
                QueueSharedCommit();
                SetStatus("Центр найден по фактическому дну; для плоского участка — точно посередине. Отметка доступна в красной ячейке; центр между точками сохраняется.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void InterpolateCrossCell()
        {
            try
            {
                int index = crossGrid.SelectedPointIndex;
                if (index < 0 || !crossGrid.IsSelectedCellEmpty) throw new ProfileValidationException("Выберите пустую ячейку отметки под нужной точкой.");
                if (crossGrid.SelectedRowKind == UI.SectionPointRow.Distance)
                    throw new ProfileValidationException("Расстояние между точками вводится по съёмке. По одним отметкам его вычислить нельзя.");
                bool design = crossGrid.SelectedRowKind == UI.SectionPointRow.Design;
                if (design && SelectedDesignMode != SectionDesignMode.Points && crossGrid.ProjectDisplayIndex == index)
                {
                    if (!currentSectionStation.HasValue) throw new ProfileValidationException("Для интерполяции проектного дна нужен пикет; либо введите отметку вручную.");
                    var sample = ProfileInterpolation.SampleLongitudinal(ReadLongitudinal(), currentSectionStation.Value);
                    if (!sample.ProjectBottom.HasValue) throw new ProfileValidationException("В продольном профиле нет двух опорных отметок проектного дна по обе стороны этого ПК.");
                    DisplayNumber(projectBottom, sample.ProjectBottom);
                    ActivateProjectBottom(sample.ProjectBottom, sectionBeforeEditing == null || !sectionBeforeEditing.ProjectBottom.HasValue);
                    RecordSharedValue(currentSectionStation.Value, SharedProfileField.ProjectBottom, sample.ProjectBottom);
                    RefreshProjectAnchorDisplay();
                    SetDirty();
                    QueueSharedCommit();
                    return;
                }
                if (design && SelectedDesignMode != SectionDesignMode.Points) throw new ProfileValidationException("Для ввода проектных отметок выберите режим «По проектным отметкам».");
                var raw = ReadCurrentSection();
                var resolved = ProfileInterpolation.ResolveSection(raw, ReadLongitudinal());
                double? value = ProfileInterpolation.SectionElevationAt(resolved.Points, resolved.Points[index].Offset, design);
                if (!value.HasValue) throw new ProfileValidationException("Нужны известные отметки по обе стороны выбранной точки и расстояния между всеми точками. За крайними опорными точками значения не вычисляются.");
                crossGrid.SetElevationAt(index, design, value.Value);
                SetDirty();
                SetStatus("Отметка интерполирована по расстояниям и записана в выбранную ячейку: " + value.Value.ToString("F2", CultureInfo.InvariantCulture) + " м.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }
}
