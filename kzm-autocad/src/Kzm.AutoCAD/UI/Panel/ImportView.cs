using System;
using System.Collections.Generic;
using System.Drawing;
using Font = System.Drawing.Font;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Import form: everything is set in the panel; the drawing is used only to point at
    /// texts / a sample marker / a position. «Проверить» shows what will happen without
    /// changing the drawing; rows can be corrected by hand; «Импортировать» writes
    /// everything in one step (one UNDO).
    /// </summary>
    internal sealed class ImportView : UserControl, IPanelView
    {
        private enum Selection { Window, Layers, Similar }

        /// <summary>Задание п. 2.1: TEXT/MTEXT; дополнительно POINT с Z и блоки с атрибутом отметки.</summary>
        private enum SourceType { Texts, Points, Blocks }

        /// <summary>What the user collected for one drawing (kept while the drawing is open).</summary>
        private sealed class State
        {
            internal string SurfaceId;
            internal SourceType Source = SourceType.Texts;
            internal List<KeyValuePair<string, string>> BlockTags = new List<KeyValuePair<string, string>>();
            internal string ElevationTag;
            internal string NumberTag;
            internal Selection Selection = Selection.Window;
            internal readonly HashSet<string> Layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal List<ObjectId> TextIds = new List<ObjectId>();
            internal string TextsNote = "тексты не выбраны";
            internal XyMode Mode = XyMode.Marker;
            internal List<MarkerCandidate> Markers;
            internal string MarkersNote = "образец не указан";
            internal KzmSettings Settings;
            internal readonly Dictionary<string, ImportOverride> Overrides = new Dictionary<string, ImportOverride>(StringComparer.OrdinalIgnoreCase);
            internal ImportResult Result;
        }

        private readonly ConditionalWeakTable<Document, State> states = new ConditionalWeakTable<Document, State>();
        private readonly ComboBox surface = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly RadioButton srcTexts = new RadioButton { Text = "Тексты", AutoSize = true, Checked = true };
        private readonly RadioButton srcPoints = new RadioButton { Text = "POINT (отметка в Z)", AutoSize = true };
        private readonly RadioButton srcBlocks = new RadioButton { Text = "Блоки с атрибутом", AutoSize = true };
        private readonly ComboBox elevationTag = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly ComboBox numberTag = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly FlowLayoutPanel tagsPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false };
        private readonly Label tagsNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText };
        private readonly RadioButton byWindow = new RadioButton { Text = "Рамкой", AutoSize = true };
        private readonly RadioButton byLayers = new RadioButton { Text = "По слоям", AutoSize = true };
        private readonly RadioButton bySimilar = new RadioButton { Text = "Подобные", AutoSize = true };
        private readonly CheckedListBox layers = new CheckedListBox { CheckOnClick = true, Height = 90, IntegralHeight = false };
        private readonly Button pickTexts = new Button { AutoSize = true };
        private readonly Label textsNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly RadioButton xyMarker = new RadioButton { Text = "По маркеру", AutoSize = true };
        private readonly RadioButton xyInsertion = new RadioButton { Text = "Точка вставки текста", AutoSize = true };
        private readonly Button pickMarker = new Button { Text = "Указать образец маркера", AutoSize = true };
        private readonly Label markersNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly Label settingsNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.Firebrick };
        private readonly Button check = new Button { Text = "Проверить", AutoSize = true };
        private readonly Button import = new Button { Text = "Импортировать", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly Label summary = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly CheckBox onlyProblems = new CheckBox { Text = "Только проблемные", AutoSize = true };
        private readonly CheckBox appearanceFromTexts = new CheckBox
        {
            Text = "Подписи точек — как у этих текстов (шрифт и высота)",
            AutoSize = true,
            MaximumSize = new Size(330, 0),
        };
        private readonly Button pickXy = new Button { Text = "Указать XY", AutoSize = true };
        private readonly Button showRow = new Button { Text = "Показать", AutoSize = true };
        private readonly DataGridView grid = new DataGridView();
        private KzmPanel panel;
        private bool binding;
        private bool filling;
        private string preselect;

        internal ImportView()
        {
            var form = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4) };
            form.Controls.Add(Header("Импорт отметок съёмки"));
            form.Controls.Add(Line("В поверхность", surface));
            surface.Width = 220;
            form.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(330, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Список ранее созданных поверхностей. Новая — кнопкой «+ Поверхность».",
            });

            form.Controls.Add(Header("1. Откуда отметки"));
            form.Controls.Add(Flow(srcTexts, srcPoints, srcBlocks));
            form.Controls.Add(Flow(byWindow, byLayers, bySimilar));
            tagsPanel.Controls.Add(Line("Атрибут отметки", elevationTag));
            tagsPanel.Controls.Add(Line("Атрибут номера", numberTag));
            tagsPanel.Controls.Add(tagsNote);
            form.Controls.Add(tagsPanel);
            form.Controls.Add(layers);
            layers.Width = 320;
            form.Controls.Add(pickTexts);
            form.Controls.Add(textsNote);

            form.Controls.Add(Header("2. Положение точки (XY)"));
            form.Controls.Add(Flow(xyMarker, xyInsertion));
            form.Controls.Add(pickMarker);
            form.Controls.Add(markersNote);
            form.Controls.Add(Header("3. Оформление и номера"));
            form.Controls.Add(appearanceFromTexts);
            form.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(330, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Номер точки берётся из того же текста («т.15 145,32») или из соседнего текста-номера («12»), иначе — по порядку. " +
                       "Цвета отметок и номеров — в свойствах поверхности.",
            });
            form.Controls.Add(settingsNote);

            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "take", HeaderText = "Взять", Width = 45 });
            foreach (var (name, header) in new[] { ("result", "Результат"), ("reason", "Причина"), ("text", "Текст"), ("z", "Отметка"), ("num", "Номер"), ("x", "X"), ("y", "Y"), ("xy", "XY") })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, ReadOnly = name != "z" });
            grid.Columns["z"].DefaultCellStyle.BackColor = Color.LightYellow;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += OnCellChanged;
            grid.CellValidating += (s, e) =>
            {
                if (grid.Columns[e.ColumnIndex].Name != "z") return;
                string text = Convert.ToString(e.FormattedValue);
                if (text.Trim().Length > 0 && !ElevationFormat.TryParse(text, out _))
                {
                    PanelMessages.Error("введите отметку числом, например 145,32.");
                    e.Cancel = true;
                }
            };
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name != "z") ShowRow(); };

            // Parameters on top (scrollable), result below; the splitter is movable.
            // Size first: WinForms rejects a SplitterDistance larger than the default 150×100 control.
            var split = new SplitContainer { Size = new Size(380, 900), Orientation = Orientation.Horizontal };
            split.SplitterDistance = 330;
            split.Dock = DockStyle.Fill;
            split.Panel1.AutoScroll = true;
            split.Panel1.Controls.Add(form);
            var actions = Flow(check, import);
            actions.Dock = DockStyle.Top;
            summary.Dock = DockStyle.Top;
            summary.Padding = new Padding(3);
            var rowTools = Flow(onlyProblems, pickXy, showRow);
            rowTools.Dock = DockStyle.Top;
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(rowTools);
            split.Panel2.Controls.Add(summary);
            split.Panel2.Controls.Add(actions);
            Controls.Add(split);

            surface.SelectedIndexChanged += (s, e) => Remember(state =>
            {
                state.SurfaceId = (surface.SelectedItem as SurfaceItem)?.Info.Id;
                state.Result = null;
                FillGrid();
            });
            srcTexts.CheckedChanged += (s, e) => SourceChanged();
            srcPoints.CheckedChanged += (s, e) => SourceChanged();
            srcBlocks.CheckedChanged += (s, e) => SourceChanged();
            elevationTag.SelectedIndexChanged += (s, e) => Remember(state =>
            {
                state.ElevationTag = elevationTag.SelectedItem as string;
                state.Result = null;
                FillGrid();
            });
            numberTag.SelectedIndexChanged += (s, e) => Remember(state =>
            {
                state.NumberTag = numberTag.SelectedIndex <= 0 ? null : numberTag.SelectedItem as string;
                state.Result = null;
                FillGrid();
            });
            byWindow.CheckedChanged += (s, e) => SelectionChanged();
            byLayers.CheckedChanged += (s, e) => SelectionChanged();
            bySimilar.CheckedChanged += (s, e) => SelectionChanged();
            layers.ItemCheck += (s, e) => BeginInvoke((Action)LayersChanged);
            pickTexts.Click += (s, e) => PickTexts();
            xyMarker.CheckedChanged += (s, e) => ModeChanged();
            pickMarker.Click += (s, e) => PickMarker();
            check.Click += (s, e) => Preview(true);
            import.Click += (s, e) => Import();
            onlyProblems.CheckedChanged += (s, e) => FillGrid();
            pickXy.Click += (s, e) => PickXy();
            showRow.Click += (s, e) => ShowRow();
        }

        private void Remember(Action<State> update)
        {
            if (binding) return;
            var state = Current;
            if (state != null) update(state);
        }

        private static Label Header(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 8, 3, 2) };

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(340, 0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        private static FlowLayoutPanel Line(string caption, Control control) =>
            Flow(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 7, 6, 0) }, control);

        private State Current
        {
            get
            {
                var document = panel?.Document;
                if (document == null) return null;
                return states.GetValue(document, _ => new State());
            }
        }

        internal void Forget(Document document) => states.Remove(document);

        internal void ResetSettings(Document document)
        {
            if (document == null || !states.TryGetValue(document, out var state)) return;
            state.Settings = null;
        }

        internal void Preselect(string surfaceId) => preselect = surfaceId;

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            var state = Current;
            binding = true;
            try
            {
                surface.Items.Clear();
                if (state == null)
                {
                    summary.Text = "Нет открытого чертежа.";
                    Enabled = false;
                    return;
                }
                Enabled = true;
                if (state.Settings == null)
                {
                    state.Settings = panel.Snapshot.Settings.Clone();
                    state.Mode = state.Settings.LastXyMode;
                }
                foreach (var item in panel.Snapshot.Surfaces) surface.Items.Add(new SurfaceItem(item.Info));
                string wanted = preselect ?? state.SurfaceId ?? panel.Snapshot.CurrentId;
                preselect = null;
                var match = surface.Items.Cast<SurfaceItem>().FirstOrDefault(item => item.Info.Id == wanted)
                            ?? surface.Items.Cast<SurfaceItem>().FirstOrDefault();
                surface.SelectedItem = match;
                state.SurfaceId = match?.Info.Id;

                srcTexts.Checked = state.Source == SourceType.Texts;
                srcPoints.Checked = state.Source == SourceType.Points;
                srcBlocks.Checked = state.Source == SourceType.Blocks;
                LoadTags(state);
                byWindow.Checked = state.Selection == Selection.Window;
                byLayers.Checked = state.Selection == Selection.Layers;
                bySimilar.Checked = state.Selection == Selection.Similar;
                xyMarker.Checked = state.Mode == XyMode.Marker;
                xyInsertion.Checked = state.Mode != XyMode.Marker;
                LoadLayers(state);
            }
            finally
            {
                binding = false;
            }
            UpdateControls();
            FillGrid();
        }

        private sealed class SurfaceItem
        {
            internal SurfaceItem(SurfaceInfo info) { Info = info; }
            internal SurfaceInfo Info { get; }
            public override string ToString() => Info.Name + " (" + SurfaceKinds.Title(Info.Kind) + ")";
        }

        private void LoadLayers(State state)
        {
            layers.Items.Clear();
            if (state.Selection != Selection.Layers) return;
            foreach (var pair in DrawingData.EntityLayers(panel.Document, Types(state)))
                layers.Items.Add(new LayerItem(pair.Key, pair.Value), state.Layers.Contains(pair.Key));
        }

        private sealed class LayerItem
        {
            internal LayerItem(string name, int count) { Name = name; Count = count; }
            internal string Name { get; }
            internal int Count { get; }
            public override string ToString() => Name + " (" + Count + ")";
        }

        private void UpdateControls()
        {
            var state = Current;
            if (state == null) return;
            bool texts = state.Source == SourceType.Texts;
            bool blocks = state.Source == SourceType.Blocks;
            byWindow.Enabled = byLayers.Enabled = !blocks;
            tagsPanel.Visible = blocks;
            layers.Visible = state.Selection == Selection.Layers && !blocks;
            pickTexts.Visible = state.Selection != Selection.Layers || blocks;
            string what = texts ? "текста" : blocks ? "блока" : "POINT";
            pickTexts.Text = state.Selection == Selection.Similar || blocks ? "Указать образец " + what
                : texts ? "Выбрать тексты в чертеже" : "Выбрать POINT в чертеже";
            textsNote.Text = "Выбрано: " + state.TextsNote;
            xyMarker.Enabled = xyInsertion.Enabled = texts;
            pickMarker.Enabled = texts && state.Mode == XyMode.Marker;
            markersNote.Text = !texts ? (blocks ? "XY = точка вставки блока; номер — из атрибута номера (если выбран)." : "XY = положение POINT, отметка = его Z.")
                : state.Mode == XyMode.Marker ? "Маркеры: " + state.MarkersNote
                : "XY = точка вставки каждого текста.";
            settingsNote.Text = surface.Items.Count == 0 ? "Сначала создайте поверхность: «+ Поверхность»." : string.Empty;
            import.Enabled = surface.SelectedItem != null;
            // Without an appearance of its own the surface takes it from the texts anyway.
            var selected = (surface.SelectedItem as SurfaceItem)?.Info;
            if (selected != null && !selected.Appearance.IsSet)
            {
                appearanceFromTexts.Checked = true;
                appearanceFromTexts.Enabled = false;
            }
            else appearanceFromTexts.Enabled = true;
        }

        private static ICollection<string> Types(State state) =>
            state.Source == SourceType.Points ? DrawingData.PointTypes : state.Source == SourceType.Blocks ? DrawingData.BlockTypes : DrawingData.TextTypes;

        private void LoadTags(State state)
        {
            elevationTag.Items.Clear();
            numberTag.Items.Clear();
            numberTag.Items.Add("(нет)");
            foreach (var pair in state.BlockTags)
            {
                elevationTag.Items.Add(pair.Key);
                numberTag.Items.Add(pair.Key);
            }
            tagsNote.Text = state.BlockTags.Count == 0 ? "Укажите образец блока — появятся его атрибуты."
                : "Атрибуты образца: " + string.Join("; ", state.BlockTags.Select(pair => pair.Key + " = «" + KzmCommands.OneLine(pair.Value) + "»")) + ".";
            elevationTag.SelectedItem = state.ElevationTag != null && elevationTag.Items.Contains(state.ElevationTag) ? state.ElevationTag : null;
            numberTag.SelectedIndex = state.NumberTag != null && numberTag.Items.Contains(state.NumberTag) ? numberTag.Items.IndexOf(state.NumberTag) : 0;
        }

        private void SourceChanged()
        {
            if (binding) return;
            var state = Current;
            if (state == null) return;
            var source = srcPoints.Checked ? SourceType.Points : srcBlocks.Checked ? SourceType.Blocks : SourceType.Texts;
            if (source == state.Source) return;
            state.Source = source;
            state.TextIds = new List<ObjectId>();
            state.TextsNote = "ничего не выбрано";
            state.Layers.Clear();
            state.Result = null;
            if (source == SourceType.Blocks)
            {
                state.Selection = Selection.Similar;
                binding = true;
                try { bySimilar.Checked = true; }
                finally { binding = false; }
            }
            LoadLayers(state);
            UpdateControls();
            FillGrid();
        }

        private void SelectionChanged()
        {
            if (binding) return;
            var state = Current;
            if (state == null) return;
            var selection = byLayers.Checked ? Selection.Layers : bySimilar.Checked ? Selection.Similar : Selection.Window;
            if (selection == state.Selection) return;
            state.Selection = selection;
            state.TextIds = new List<ObjectId>();
            state.TextsNote = "тексты не выбраны";
            state.Result = null;
            LoadLayers(state);
            UpdateControls();
            FillGrid();
        }

        private void LayersChanged()
        {
            var state = Current;
            if (state == null || binding) return;
            state.Layers.Clear();
            foreach (LayerItem item in layers.CheckedItems) state.Layers.Add(item.Name);
            state.TextIds = DrawingData.EntitiesOnLayers(panel.Document, state.Layers, Types(state));
            state.TextsNote = ImportService.CountTexts(panel.Document.Database, state.TextIds) +
                              (state.Layers.Count > 0 ? " на слоях " + string.Join(", ", state.Layers) : string.Empty);
            state.Result = null;
            UpdateControls();
            FillGrid();
        }

        private void ModeChanged()
        {
            if (binding) return;
            var state = Current;
            if (state == null) return;
            state.Mode = xyMarker.Checked ? XyMode.Marker : XyMode.Insertion;
            state.Result = null;
            UpdateControls();
            FillGrid();
        }

        private void PickTexts()
        {
            var state = Current;
            if (state == null) return;
            List<ObjectId> picked = null;
            List<KeyValuePair<string, string>> tags = null;
            string note = null;
            bool similar = state.Selection == Selection.Similar;
            var source = state.Source;
            PanelActions.Pick("выбор источников отметок", document =>
            {
                if (source == SourceType.Blocks)
                {
                    var (ids, description, found) = ImportService.PickSimilarBlocks(document);
                    picked = ids;
                    note = description;
                    tags = found;
                }
                else if (source == SourceType.Points)
                {
                    if (similar)
                    {
                        var (ids, description) = ImportService.PickSimilarPoints(document);
                        picked = ids;
                        note = description;
                    }
                    else picked = ImportService.PickPointObjects(document.Editor);
                }
                else if (similar)
                {
                    var (ids, description) = ImportService.PickSimilarTexts(document);
                    picked = ids;
                    note = description;
                }
                else
                {
                    picked = ImportService.PickTexts(document.Editor);
                }
            }, () =>
            {
                if (picked == null) return;
                if (tags != null)
                {
                    state.BlockTags = tags;
                    state.ElevationTag = ImportService.GuessElevationTag(tags);
                    state.NumberTag = null;
                    binding = true;
                    try { LoadTags(state); }
                    finally { binding = false; }
                }
                state.TextIds = picked;
                state.TextsNote = ImportService.CountTexts(panel.Document.Database, picked) + (note != null ? " — подобные: " + note : string.Empty);
                state.Result = null;
                UpdateControls();
                FillGrid();
                PanelMessages.Info("Тексты выбраны. Нажмите «Проверить».");
            });
        }

        private void PickMarker()
        {
            var state = Current;
            if (state == null) return;
            List<MarkerCandidate> markers = null;
            string note = null;
            PanelActions.Pick("образец маркера", document =>
            {
                var (found, description) = ImportService.PickMarkers(document);
                markers = found;
                note = description;
            }, () =>
            {
                if (markers == null) return;
                state.Markers = markers;
                state.MarkersNote = markers.Count + " (" + note + ")";
                state.Result = null;
                UpdateControls();
                FillGrid();
            });
        }

        /// <summary>Takes the form values into the state; returns false and tells why when something is wrong.</summary>
        private bool ReadForm(State state, out ImportRequest request)
        {
            request = null;
            var errors = new List<string>();
            var settings = state.Settings.Clone();
            settings.LastXyMode = state.Mode;
            settings.LastSelectionMode = state.Selection.ToString().ToUpperInvariant();
            var surfaceItem = surface.SelectedItem as SurfaceItem;
            if (surfaceItem == null) errors.Add("выберите поверхность (или создайте: «+ Поверхность»)");
            if (state.TextIds.Count == 0) errors.Add(state.Source == SourceType.Texts ? "выберите тексты" : state.Source == SourceType.Points ? "выберите POINT" : "укажите образец блока");
            if (state.Source == SourceType.Texts && state.Mode == XyMode.Marker && state.Markers == null) errors.Add("укажите образец маркера");
            if (state.Source == SourceType.Blocks && string.IsNullOrEmpty(state.ElevationTag)) errors.Add("выберите атрибут отметки");
            if (errors.Count > 0)
            {
                PanelMessages.Error(string.Join("; ", errors.Distinct()) + ".");
                return false;
            }
            state.Settings = settings;
            state.SurfaceId = surfaceItem.Info.Id;
            request = new ImportRequest
            {
                SurfaceId = surfaceItem.Info.Id,
                TextIds = state.TextIds.ToList(),
                Markers = state.Source == SourceType.Texts ? state.Markers ?? new List<MarkerCandidate>() : new List<MarkerCandidate>(),
                Mode = state.Source == SourceType.Texts ? state.Mode : XyMode.Insertion,
                ElevationTag = state.ElevationTag,
                NumberTag = state.NumberTag,
                Settings = settings,
                Overrides = state.Overrides,
                AppearanceFromTexts = appearanceFromTexts.Checked,
            };
            return true;
        }

        /// <summary>«Проверить»: the plan without changing the drawing.</summary>
        private void Preview(bool announce)
        {
            var state = Current;
            if (state == null || !ReadForm(state, out var request)) return;
            try
            {
                using (panel.Document.LockDocument(DocumentLockMode.Read, null, null, false))
                    state.Result = ImportService.Prepare(panel.Document.Database, request);
                if (announce) PanelMessages.Info("Проверка выполнена, чертёж не изменён. Исправьте строки при необходимости и нажмите «Импортировать».");
            }
            catch (KzmException exception)
            {
                state.Result = null;
                PanelMessages.Error(exception.Message);
            }
            FillGrid();
        }

        private void Import()
        {
            var state = Current;
            if (state == null || !ReadForm(state, out var request)) return;
            ImportResult result = null;
            PanelActions.Run("импорт отметок", document =>
            {
                result = ImportService.Commit(document, request);
                foreach (var line in result.Summary) document.Editor.WriteMessage("\nКЗМ: " + line);
                var problems = result.Plan.Rejected.Concat(result.Plan.Disputed)
                    .Select(item => result.TextIds.TryGetValue(item.Source.Handle, out var id) ? id : ObjectId.Null)
                    .Where(id => id.IsValid).ToArray();
                if (problems.Length > 0) document.Editor.SetImpliedSelection(problems);
            }, () =>
            {
                if (result == null) return;
                state.Result = result;
                state.Overrides.Clear();
                PanelMessages.Info("Импорт выполнен: создано точек " + result.Created.Count + ". Отменить — U.");
                panel.RefreshAll();
            });
        }

        private void FillGrid()
        {
            filling = true;
            try { FillRows(); }
            finally { filling = false; }
        }

        private void FillRows()
        {
            var state = Current;
            grid.Rows.Clear();
            if (state?.Result == null)
            {
                summary.Text = state == null ? string.Empty : "Нажмите «Проверить» — модуль покажет, что будет импортировано, не меняя чертёж.";
                return;
            }
            var result = state.Result;
            summary.Text = string.Join(Environment.NewLine, result.Summary);
            int decimals = state.Settings.Decimals;
            foreach (var item in result.Plan.Items)
            {
                bool problem = item.Status == ImportStatus.Rejected || item.Status == ImportStatus.Disputed;
                bool warning = item.Status == ImportStatus.Accepted && item.Warning != null;
                if (onlyProblems.Checked && !problem && !warning) continue;
                bool created = result.Committed && result.Created.ContainsKey(item);
                bool numberText = item.NumberFor != null;
                if (numberText) problem = false;
                if (onlyProblems.Checked && numberText) continue;
                string status = item.Status == ImportStatus.Accepted ? (result.Committed ? (created ? "создано" : "принято") : "будет создано")
                    : numberText ? "номер точки"
                    : item.Status == ImportStatus.Rejected ? "отброшено"
                    : item.Status == ImportStatus.Disputed ? "спорная"
                    : "пропущено";
                string z = item.Elevation.HasValue ? ElevationFormat.Format(item.Elevation.Value, decimals)
                    : string.Join("; ", item.Candidates.Select(value => ElevationFormat.Format(value, decimals)));
                var position = item.Position ?? item.Source.Anchor;
                string xy = item.Position.HasValue
                    ? XyModes.Title(item.PositionMode) + (item.PositionMode == XyMode.Marker && item.MarkerDistance.HasValue
                        ? " " + ElevationFormat.Format(item.MarkerDistance.Value, 2) + " м" : string.Empty)
                    : string.Empty;
                string reason = item.Warning == null ? item.Message
                    : (string.IsNullOrEmpty(item.Message) ? item.Warning : item.Message + "; " + item.Warning);
                if (numberText) reason = "номер для отметки «" + KzmCommands.OneLine(item.NumberFor.Source.Plain) + "»";
                if (numberText) z = string.Empty;
                int index = grid.Rows.Add(item.Status == ImportStatus.Accepted, status, reason, KzmCommands.OneLine(item.Source.Plain), z,
                    item.Number ?? (numberText ? item.Integers[0] : string.Empty),
                    ElevationFormat.Format(position.X, 3), ElevationFormat.Format(position.Y, 3), xy);
                var row = grid.Rows[index];
                row.Tag = item;
                bool editable = !result.Committed && (item.Status != ImportStatus.Skipped || item.Reason == ImportReason.ManualSkipped);
                row.Cells["take"].ReadOnly = result.Committed || !(item.Status == ImportStatus.Accepted || item.Reason == ImportReason.ManualSkipped);
                row.Cells["z"].ReadOnly = result.Committed || !editable;
                if (problem) row.DefaultCellStyle.ForeColor = Color.Firebrick;
                if (numberText) row.DefaultCellStyle.ForeColor = Color.SteelBlue;
                if (warning) row.DefaultCellStyle.ForeColor = Color.DarkOrange;
                if (item.Status == ImportStatus.Skipped) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            foreach (var duplicate in result.Duplicates)
            {
                int index = grid.Rows.Add(false, "близкие точки",
                    "расстояние " + ElevationFormat.Format(duplicate.Distance, 3) + " м — обе будут созданы, решение в «Близкие точки»",
                    KzmCommands.OneLine(duplicate.First.Source.Plain), string.Empty, string.Empty,
                    ElevationFormat.Format(duplicate.First.Position.Value.X, 3), ElevationFormat.Format(duplicate.First.Position.Value.Y, 3), string.Empty);
                grid.Rows[index].ReadOnly = true;
                grid.Rows[index].DefaultCellStyle.ForeColor = Color.DarkOrange;
                grid.Rows[index].Tag = duplicate.First;
            }
        }

        private ImportItem CurrentItem => grid.CurrentRow?.Tag as ImportItem;

        private ImportOverride OverrideFor(State state, ImportItem item)
        {
            if (!state.Overrides.TryGetValue(item.Source.Handle, out var decision))
                state.Overrides[item.Source.Handle] = decision = new ImportOverride();
            return decision;
        }

        private void OnCellChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || filling) return;
            var state = Current;
            var item = grid.Rows[e.RowIndex].Tag as ImportItem;
            if (state?.Result == null || state.Result.Committed || item == null) return;
            string column = grid.Columns[e.ColumnIndex].Name;
            var decision = OverrideFor(state, item);
            if (column == "take")
            {
                decision.Exclude = !(grid.Rows[e.RowIndex].Cells["take"].Value is bool take && take);
            }
            else if (column == "z")
            {
                string text = Convert.ToString(grid.Rows[e.RowIndex].Cells["z"].Value);
                if (text.Trim().Length == 0) decision.Elevation = null;
                else if (ElevationFormat.TryParse(text, out double value)) decision.Elevation = value;
                else return;
            }
            else return;
            if (decision.IsEmpty) state.Overrides.Remove(item.Source.Handle);
            BeginInvoke((Action)(() => Preview(false)));
        }

        private void PickXy()
        {
            var state = Current;
            var item = CurrentItem;
            if (state?.Result == null || item == null)
            {
                PanelMessages.Error("сначала «Проверить», затем выберите строку.");
                return;
            }
            if (state.Result.Committed || (item.Status == ImportStatus.Skipped && item.Reason != ImportReason.ManualSkipped))
            {
                PanelMessages.Error("для этой строки положение не задаётся (уже импортирована или импорт выполнен — нажмите «Проверить»).");
                return;
            }
            XY? picked = null;
            string label = KzmCommands.OneLine(item.Source.Plain);
            var textId = state.Result.TextIds.TryGetValue(item.Source.Handle, out var id) ? id : ObjectId.Null;
            PanelActions.Pick("положение точки", document =>
            {
                if (textId.IsValid) KzmCommands.Highlight(document.Database, new[] { textId }, true);
                try { picked = ImportService.PickPoint(document.Editor, "Укажите положение точки для текста «" + label + "»"); }
                finally { if (textId.IsValid) KzmCommands.Highlight(document.Database, new[] { textId }, false); }
            }, () =>
            {
                if (!picked.HasValue) return;
                var decision = OverrideFor(state, item);
                decision.Position = picked;
                decision.PositionMode = XyMode.Manual;
                Preview(false);
            });
        }

        private void ShowRow()
        {
            var state = Current;
            var item = CurrentItem;
            if (state?.Result == null || item == null) return;
            var ids = new List<ObjectId>();
            if (state.Result.TextIds.TryGetValue(item.Source.Handle, out var textId)) ids.Add(textId);
            if (state.Result.Created.TryGetValue(item, out var pointId)) ids.Add(pointId);
            if (item.ExistingPointHandle != null && state.Result.ExistingIds.TryGetValue(item.ExistingPointHandle, out var existing)) ids.Add(existing);
            PanelActions.Show(panel.Document, ids, KzmCommands.ZoomSize(panel.Snapshot.Find(state.SurfaceId)?.Info.Appearance.TextHeight));
        }
    }
}
