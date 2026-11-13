using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Картограмма: граница работ, поверхности для расчёта, сетка квадратов, оформление и подписи
    /// в узлах, проверка без построения, построение/пересчёт, итоги с контрольным расчётом,
    /// замечания с переходом к месту, ведомость объёмов и выгрузка в Excel.
    /// </summary>
    internal sealed class CartogramView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly TextBox name = new TextBox { Width = 220 };
        private readonly ComboBox fact = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly ComboBox project = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly Label boundaryNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        /// <summary>Части границы работ (замечание 31): одна общая картограмма по всем.</summary>
        private readonly ListBox boundaryList = new ListBox { Width = 330, Height = 64, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly TextBox step = new TextBox { Width = 70 };
        private readonly RadioButton originAuto = new RadioButton { Text = "Авто (кратно шагу)", AutoSize = true };
        private readonly RadioButton originPoint = new RadioButton { Text = "Точка", AutoSize = true };
        private readonly TextBox originX = new TextBox { Width = 110 };
        private readonly TextBox originY = new TextBox { Width = 110 };
        private readonly TextBox tableGap = new TextBox { Width = 60 };
        private readonly ComboBox gridWeight = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly Button boundaryButton = new Button { Text = "Добавить границу ▾", AutoSize = true };
        private readonly ComboBox scale = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        private readonly TextBox nodeText = new TextBox { Width = 60 };
        private readonly TextBox volumeText = new TextBox { Width = 60 };
        private readonly TextBox totalsText = new TextBox { Width = 60 };
        private readonly NumericUpDown volumeDecimals = new NoWheelNumericUpDown { Minimum = 0, Maximum = 3, Width = 60 };
        private readonly ComboBox[] corners = new ComboBox[3];
        private readonly Button[] colors = new Button[3];
        private readonly ComboBox[] layers = new ComboBox[3];
        private readonly string[] colorValues = { PointAppearance.ByLayer, PointAppearance.ByLayer, PointAppearance.ByLayer };
        private readonly CheckBox control = new CheckBox { Text = "Контрольный расчёт по наложению поверхностей (п. 5)", AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly CheckBox keepMoved = new CheckBox { Text = "При пересчёте ставить сдвинутые вручную подписи на их место (п. 7)", AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly ListBox problems = new ListBox { Width = 330, Height = 90, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Button marks = new Button { Text = "1. Сетка и отметки", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly Button build = new Button { Text = "2. Рассчитать объёмы", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly TextBox hatchSpacing = new TextBox { Width = 60 };
        private readonly ComboBox hatchWeight = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly Button preview = new Button { Text = "Показать сетку", AutoSize = true };
        private readonly List<Entity> previewEntities = new List<Entity>();
        private KzmPanel panel;
        private CartogramSummary cartogram;
        private bool creating;
        private bool newMode;
        private List<string> boundaryHandles = new List<string>();
        /// <summary>Scale the text heights were shown for (−1 while the form is being filled).</summary>
        private int shownScale = -1;
        private string lastBound;
        private CartogramComputation lastComputation;
        private ContextMenuStrip boundaryMenu;
        /// <summary>Id of a new cartogram before it is saved (the boundary drawn by points is tagged with it).</summary>
        private string draftId;

        internal CartogramView()
        {
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Span(Control c) { table.Controls.Add(c); table.SetColumnSpan(c, 2); }
            void Row(string caption, Control c)
            {
                table.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(150, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) });
                table.Controls.Add(c);
            }
            Label Section(string text) => new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };
            Label Hint(string text) => new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText, Text = text };
            Button Action(string text, EventHandler click)
            {
                var button = new Button { Text = text, AutoSize = true };
                button.Click += click;
                return button;
            }

            Span(title);
            Row("Имя", name);

            Span(Section("Поверхности для расчёта"));
            Row("Фактическая (Hф)", fact);
            Row("Проектная (Hпр)", project);
            Span(Hint("Отметки в узлах сетки и в точках границы, где съёмки нет, интерполируются по треугольникам поверхностей; h = Hпр − Hф."));

            Span(Section("Граница работ"));
            Span(boundaryNote);
            Span(boundaryList);
            boundaryList.DoubleClick += (s, e) => ShowBoundary();
            boundaryButton.Click += (s, e) =>
            {
                boundaryMenu?.Dispose();
                boundaryMenu = BoundaryMenu();
                boundaryMenu.Show(boundaryButton, 0, boundaryButton.Height);
            };
            Span(Flow(boundaryButton, Action("Показать", (s, e) => ShowBoundary()), Action("Убрать", (s, e) => RemoveBoundary())));
            Span(Hint("Границ может быть несколько (отдельные площадки): картограмма одна общая — общая сетка, одна таблица, " +
                      "объёмы и площади по всем частям. Части не должны перекрываться.\n" +
                      "По точкам: укажите точки границы по очереди — отметка берётся у существующей точки или по выбранной поверхности, " +
                      "её можно заменить. Точки границы войдут в проектную поверхность, сама граница — её структурной линией."));

            Span(Section("Сетка квадратов"));
            Row("Шаг, м", step);
            Row("Базовая точка", Flow(originAuto, originPoint));
            Row("X / Y", Flow(originX, originY, Action("Указать…", (s, e) => PickOrigin())));
            foreach (int weight in CartogramInfo.LineWeights) gridWeight.Items.Add(ElevationFormat.Format(weight / 100.0, 2) + " мм");
            Row("Толщина линий сетки", gridWeight);
            preview.Click += (s, e) => TogglePreview();
            Span(preview);

            Span(Section("Оформление (СТБ 2073-2010)"));
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            scale.SelectedIndexChanged += (s, e) => ScaleChanged();
            Row("Масштаб", scale);
            Row("Текст отметок в узлах, мм", nodeText);
            Row("Текст объёмов, мм", volumeText);
            Row("Текст итогов, мм", totalsText);
            Span(Hint("Высоты — в мм на листе. По умолчанию 0,9 в чертеже: 0,9 мм при 1:1000, 1,8 мм при 1:500 (при смене масштаба " +
                      "пересчитываются, если вы их не меняли). Шрифт — в «Настройках»."));
            Row("Объёмы: знаков после запятой", volumeDecimals);
            Row("Зазор от сетки до таблицы, мм", tableGap);
            Row("Шаг штриховки выемки, мм", hatchSpacing);
            foreach (int weight in CartogramInfo.LineWeights) hatchWeight.Items.Add(ElevationFormat.Format(weight / 100.0, 2) + " мм");
            Row("Толщина линий штриховки", hatchWeight);

            Span(Section("Подписи в узле"));
            string[] titles = { "Рабочая (h)", "Проектная (Hпр)", "Фактическая (Hф)" };
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                corners[i] = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
                foreach (LabelCorner corner in Enum.GetValues(typeof(LabelCorner))) corners[i].Items.Add(NodeLabelStyle.Title(corner));
                colors[i] = new Button { AutoSize = true, Text = "ПоСлою" };
                colors[i].Click += (s, e) => PickColor(index);
                layers[i] = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150 };
                if (i == 0)
                {
                    // Рабочая отметка: насыпь — красным, выемка — синим.
                    colors[i].Visible = false;
                    Row(titles[i], Flow(corners[i], new Label { Text = "цвет по знаку", AutoSize = true, Margin = new Padding(3, 7, 3, 0) }, layers[i]));
                }
                else Row(titles[i], Flow(corners[i], colors[i], layers[i]));
            }
            Span(Hint("По умолчанию — как в примере Б.4 СТБ 2073-2010: рабочая — слева вверху, проектная — справа вверху, фактическая — справа внизу. " +
                      "Отметки подписываются в узлах внутри границы и там, где сетка пересекает границу; близкие подписи разводятся с выноской. " +
                      "Отметки — блок «КЗМ_УЗЕЛ»: проектную и фактическую можно поправить в «Быстрых свойствах» — при пересчёте они запишутся в поверхности. " +
                      "Насыпь — красным, выемка — синим, выемки заштрихованы под 45° (п. 7.1). Сетка — один зелёный блок, только внутри границы. " +
                      "Таблица объёмов (рис. 8) — один блок ниже сетки. Высоты текста — из ряда ГОСТ 2.304, их можно менять."));

            Span(Section("Расчёт"));
            Span(control);
            Span(keepMoved);

            marks.Click += (s, e) => Save(Step.Marks);
            build.Click += (s, e) => Save(Step.Volumes);
            Span(Hint("Шаг 1 — сетка и отметки по углам. Проверьте их, при необходимости исправьте проектные и фактические отметки " +
                      "в «Свойствах» (рабочая пересчитается сразу) и раздвиньте подписи. Шаг 2 — объёмы, штриховка, линия нулевых работ, таблица и площади; " +
                      "ваши исправления отметок при этом запишутся в поверхности."));
            Span(Flow(marks, build));
            Span(Flow(Action("Проверить", (s, e) => Check()), Action("Сохранить", (s, e) => Save(Step.None))));
            Span(Flow(Action("Показать на чертеже", (s, e) => ShowDrawing()), Action("В Excel…", (s, e) => Export(panel, cartogram?.Info.Id)),
                Action("Ведомость объёмов…", (s, e) => Statement(panel, cartogram?.Info.Id))));
            Span(Flow(Action("Стереть построение", (s, e) => EraseDrawing()), Action("Сделать шаблоном", (s, e) => MakeTemplate())));
            var deleteButton = new Button { Text = "Удалить картограмму", AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 10, 3, 3) };
            deleteButton.Click += (s, e) => Delete(panel, cartogram?.Info.Id);
            Span(deleteButton);
            Span(Section("Итоги"));
            Span(status);
            Span(problems);
            Span(Flow(Action("Показать место", (s, e) => ShowProblem()),
                new Label { AutoSize = true, Text = "двойной щелчок по замечанию — показать", ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 3, 0) }));
            problems.DoubleClick += (s, e) => ShowProblem();

            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            root.Controls.Add(table);
            Controls.Add(root);
            originAuto.CheckedChanged += (s, e) => originX.Enabled = originY.Enabled = originPoint.Checked;
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(340, 0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        internal void PrepareNew() => creating = true;

        public void Bind(KzmPanel owner, string cartogramId)
        {
            panel = owner;
            // Each list is filled in one go (one repaint instead of one per item, замечание 28).
            fact.BeginUpdate();
            project.BeginUpdate();
            fact.Items.Clear();
            project.Items.Clear();
            fact.Items.AddRange(panel.Snapshot.Surfaces.Where(s => s.Info.Kind != SurfaceKind.Project).Select(s => (object)new SurfaceItem(s.Info)).ToArray());
            project.Items.AddRange(panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Project).Select(s => (object)new SurfaceItem(s.Info)).ToArray());
            // Either kind may be used on either side if the user wants it (e.g. two project variants).
            fact.Items.AddRange(panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Project).Select(s => (object)new SurfaceItem(s.Info, true)).ToArray());
            project.Items.AddRange(panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Fact).Select(s => (object)new SurfaceItem(s.Info, true)).ToArray());
            fact.EndUpdate();
            project.EndUpdate();
            var layerNames = new[] { NodeLabelStyle.WorkingLayer, NodeLabelStyle.ProjectLayer, NodeLabelStyle.FactLayer }.Concat(panel.Snapshot.Layers.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(l => (object)l).ToArray();
            foreach (var box in layers)
            {
                string typed = box.Text;
                box.BeginUpdate();
                box.Items.Clear();
                box.Items.AddRange(layerNames);
                box.EndUpdate();
                box.Text = typed;
            }

            if (cartogramId == null)
            {
                // A refresh of the panel keeps what the user is typing into the new-cartogram form.
                if (newMode && !creating) return;
                creating = false;
                newMode = true;
                cartogram = null;
                lastBound = null;
                boundaryHandles = new List<string>();
                lastComputation = null;
                draftId = Guid.NewGuid().ToString("N");
                var info = new CartogramInfo
                {
                    Id = draftId,
                    ProjectId = panel.Snapshot.Project == null || panel.Snapshot.Project.Id == ProjectInfo.LegacyId ? null : panel.Snapshot.Project.Id,
                    Name = "Картограмма " + (panel.Snapshot.Cartograms.Count + 1),
                    Scale = panel.Snapshot.Settings.Scale,
                    FactSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Fact)?.Info.Id,
                    ProjectSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Project)?.Info.Id,
                };
                // 0,9 in the drawing at any scale (замечание 29); a saved template may set its own heights.
                info.NodeTextMm = info.VolumeTextMm = info.TotalsTextMm = CartogramInfo.DefaultTextMm(info.Scale);
                var defaults = KzmDefaults.Load(out string problem);
                if (defaults.HasValue) info.TakeTemplate(defaults.Value.Template);
                if (problem != null) PanelMessages.Error(problem);
                Display(info, true);
                return;
            }
            newMode = false;
            cartogram = panel.Snapshot.FindCartogram(cartogramId);
            if (cartogram == null)
            {
                title.Text = "Картограмма не найдена";
                status.Text = string.Empty;
                problems.Items.Clear();
                return;
            }
            if (lastBound != cartogramId)
            {
                boundaryHandles = cartogram.Info.BoundaryHandles.ToList();
                lastComputation = null;
            }
            lastBound = cartogramId;
            Display(cartogram.Info, false);
        }

        private void Display(CartogramInfo info, bool isNew)
        {
            title.Text = isNew ? "Новая картограмма" : "Картограмма";
            name.Text = info.Name;
            Select(fact, info.FactSurfaceId);
            Select(project, info.ProjectSurfaceId);
            ShowBoundaries(null);
            step.Text = SettingsView.FormatValue(info.Step);
            originAuto.Checked = !(info.OriginX.HasValue && info.OriginY.HasValue);
            originPoint.Checked = !originAuto.Checked;
            originX.Text = SettingsView.FormatValue(info.OriginX);
            originY.Text = SettingsView.FormatValue(info.OriginY);
            originX.Enabled = originY.Enabled = originPoint.Checked;
            tableGap.Text = SettingsView.FormatValue(info.TableGapMm);
            gridWeight.SelectedIndex = Math.Max(0, Array.IndexOf(CartogramInfo.LineWeights, info.GridLineWeight));
            shownScale = -1;
            scale.SelectedIndex = Math.Max(0, Array.IndexOf(KzmSettings.Scales, info.Scale));
            shownScale = KzmSettings.Scales[Math.Max(0, scale.SelectedIndex)];
            nodeText.Text = SettingsView.FormatValue(info.NodeTextMm);
            volumeText.Text = SettingsView.FormatValue(info.VolumeTextMm);
            totalsText.Text = SettingsView.FormatValue(info.TotalsTextMm);
            volumeDecimals.Value = Math.Max(0, Math.Min(3, info.VolumeDecimals));
            var labels = new[] { info.WorkingLabel, info.ProjectLabel, info.FactLabel };
            for (int i = 0; i < 3; i++)
            {
                corners[i].SelectedIndex = (int)labels[i].Corner;
                layers[i].Text = labels[i].Layer;
                colorValues[i] = labels[i].Color ?? PointAppearance.ByLayer;
                ShowColor(colors[i], colorValues[i]);
            }
            control.Checked = info.ControlCheck;
            keepMoved.Checked = info.KeepMovedLabels;
            hatchSpacing.Text = SettingsView.FormatValue(info.HatchSpacingMm);
            hatchWeight.SelectedIndex = Math.Max(0, Array.IndexOf(CartogramInfo.LineWeights, info.HatchLineWeight));
            ShowResults(info, isNew);
        }

        private void ShowResults(CartogramInfo info, bool isNew)
        {
            problems.Items.Clear();
            bool outdated = cartogram != null && cartogram.Outdated;
            if (lastComputation != null && lastComputation.Info.Id == info.Id)
            {
                status.Text = (info.Built && !outdated ? "Построена " + info.BuiltAt + ".\n" : lastComputation == null ? string.Empty : "Проверка (чертёж не изменён):\n") +
                              string.Join("\n", CartogramService.Summary(lastComputation));
                foreach (var (text, at) in lastComputation.Marks) problems.Items.Add(new ProblemItem(text, at));
                foreach (var problem in lastComputation.Problems) problems.Items.Add(new ProblemItem(problem, null));
            }
            else if (isNew) status.Text = "Заполните поля и нажмите «1. Сетка и отметки» («Проверить» считает, не меняя чертёж).";
            else if (info.Built && !info.VolumesDrawn)
                status.Text = "Нанесены сетка и отметки (" + info.BuiltAt + "). Проверьте отметки и нажмите «2. Рассчитать объёмы».";
            else if (!info.Built) status.Text = "Ещё не построена. «Проверить» посчитает объёмы, не меняя чертёж.";
            else
            {
                var lines = new List<string>
                {
                    "Построена " + info.BuiltAt + ": насыпь " + CartogramService.Volume(info.LastFill, info.VolumeDecimals, true) +
                    " м³, выемка " + CartogramService.Volume(info.LastCut, info.VolumeDecimals, false) + " м³; баланс " +
                    ElevationFormat.FormatSigned(info.LastFill - info.LastCut, info.VolumeDecimals) + " м³.",
                };
                if (info.LastSquares > 0 && info.LastArea.HasValue)
                    lines.Add("Квадратов " + info.LastSquares + ", площадь " + ElevationFormat.Format(info.LastArea.Value, 1) + " м².");
                if (info.LastControlFill.HasValue && info.LastControlCut.HasValue)
                    lines.Add("Контрольный расчёт: насыпь " + ElevationFormat.Format(info.LastControlFill.Value, 1) + ", выемка " +
                              ElevationFormat.Format(info.LastControlCut.Value, 1) + " м³.");
                status.Text = string.Join("\n", lines);
            }
            if (outdated)
                status.Text += cartogram.LabelEdits > 0
                    ? "\nИсправлены отметки на картограмме (" + cartogram.LabelEdits + ") — «2. Рассчитать объёмы» запишет их в поверхности и пересчитает объёмы."
                    : info.OldDrawing
                        ? "\nПостроена прежней версией модуля — постройте заново («1. Сетка и отметки», затем «2. Рассчитать объёмы»), чтобы получить новое оформление."
                        : "\nУСТАРЕЛА: точки, структурные линии, границы или параметры изменились — постройте заново.";
            status.ForeColor = outdated ? Color.Firebrick : SystemColors.ControlText;
            if (problems.Items.Count == 0) problems.Items.Add(new ProblemItem(lastComputation != null ? "Замечаний нет." : "Замечания появятся после проверки или построения.", null));
        }

        private sealed class ProblemItem
        {
            internal ProblemItem(string text, XY? at) { Text = text; At = at; }
            internal string Text { get; }
            internal XY? At { get; }
            public override string ToString() => (At.HasValue ? "▶ " : "• ") + Text;
        }

        private sealed class SurfaceItem
        {
            internal SurfaceItem(SurfaceInfo info, bool other = false) { Info = info; Other = other; }
            internal SurfaceInfo Info { get; }
            internal bool Other { get; }
            public override string ToString() => Info.Name + (Other ? " (" + SurfaceKinds.Title(Info.Kind) + ")" : string.Empty);
        }

        private static void Select(ComboBox box, string id)
        {
            box.SelectedItem = box.Items.Cast<SurfaceItem>().FirstOrDefault(item => item.Info.Id == id);
        }

        private static void ShowColor(Button button, string value)
        {
            var color = PointBlocks.ParseColor(value);
            button.Text = color.IsByLayer ? "ПоСлою" : color.IsByAci ? "Индекс " + color.ColorIndex : "RGB " + color.Red + "," + color.Green + "," + color.Blue;
            button.BackColor = color.IsByLayer ? SystemColors.Control : color.ColorValue;
            button.ForeColor = color.IsByLayer || color.ColorValue.GetBrightness() > 0.5 ? Color.Black : Color.White;
        }

        private void PickColor(int index)
        {
            var dialog = new Autodesk.AutoCAD.Windows.ColorDialog { Color = PointBlocks.ParseColor(colorValues[index]), IncludeByBlockByLayer = true };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            colorValues[index] = PointBlocks.FormatColor(dialog.Color);
            ShowColor(colors[index], colorValues[index]);
        }

        private CartogramInfo Read(List<string> errors)
        {
            var info = cartogram?.Info.Clone() ?? new CartogramInfo
            {
                Id = draftId ?? (draftId = Guid.NewGuid().ToString("N")),
                ProjectId = panel?.Snapshot.Project == null || panel.Snapshot.Project.Id == ProjectInfo.LegacyId ? null : panel.Snapshot.Project.Id,
            };
            info.Name = name.Text.Trim();
            info.FactSurfaceId = (fact.SelectedItem as SurfaceItem)?.Info.Id;
            info.ProjectSurfaceId = (project.SelectedItem as SurfaceItem)?.Info.Id;
            info.BoundaryHandles = boundaryHandles;
            info.Step = SettingsView.Parse(step.Text, "шаг", errors) ?? 0;
            if (originPoint.Checked)
            {
                info.OriginX = SettingsView.Parse(originX.Text, "X базовой точки", errors);
                info.OriginY = SettingsView.Parse(originY.Text, "Y базовой точки", errors);
                if (!info.OriginX.HasValue || !info.OriginY.HasValue) errors.Add("укажите базовую точку или выберите «Авто»");
            }
            else info.OriginX = info.OriginY = null;
            info.AngleDegrees = 0;
            info.TableGapMm = SettingsView.Parse(tableGap.Text, "зазор до таблицы", errors) ?? 10;
            info.GridLineWeight = CartogramInfo.LineWeights[Math.Max(0, gridWeight.SelectedIndex)];
            info.HatchSpacingMm = SettingsView.Parse(hatchSpacing.Text, "шаг штриховки", errors) ?? 4;
            info.HatchLineWeight = CartogramInfo.LineWeights[Math.Max(0, hatchWeight.SelectedIndex)];
            info.Scale = KzmSettings.Scales[Math.Max(0, scale.SelectedIndex)];
            info.NodeTextMm = SettingsView.Parse(nodeText.Text, "текст отметок", errors) ?? 0;
            info.VolumeTextMm = SettingsView.Parse(volumeText.Text, "текст объёмов", errors) ?? 0;
            info.TotalsTextMm = SettingsView.Parse(totalsText.Text, "текст итогов", errors) ?? 0;
            info.VolumeDecimals = (int)volumeDecimals.Value;
            var labels = new[] { info.WorkingLabel, info.ProjectLabel, info.FactLabel };
            for (int i = 0; i < 3; i++)
            {
                labels[i].Corner = (LabelCorner)Math.Max(0, corners[i].SelectedIndex);
                labels[i].Layer = layers[i].Text.Trim();
                labels[i].Color = colorValues[i];
            }
            info.ControlCheck = control.Checked;
            info.KeepMovedLabels = keepMoved.Checked;
            if (info.FactSurfaceId != null && info.FactSurfaceId == info.ProjectSurfaceId) errors.Add("фактическая и проектная поверхности совпадают");
            errors.AddRange(info.Validate());
            return info;
        }

        private bool TryRead(out CartogramInfo info)
        {
            var errors = new List<string>();
            info = Read(errors);
            if (errors.Count == 0) return true;
            status.Text = "Исправьте: " + string.Join("; ", errors.Distinct()) + ".";
            status.ForeColor = Color.Firebrick;
            return false;
        }

        private enum Step { None, Marks, Volumes }

        private void Save(Step step)
        {
            if (panel?.Document == null) return;
            if (panel.Snapshot.Project == null) { PanelMessages.Error("сначала создайте или откройте проект (кнопка «Проект» вверху панели)."); return; }
            if (!TryRead(out var info)) return;
            ClearPreview();
            var settings = panel.Snapshot.Settings;
            CartogramComputation computation = null;
            PanelActions.Run(step == Step.None ? "сохранение картограммы" : step == Step.Marks ? "сетка и отметки" : "объёмы картограммы", document =>
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    KzmStore.WriteCartogram(document.Database, transaction, info);
                    transaction.Commit();
                }
                if (step == Step.None)
                {
                    PanelMessages.Info("Картограмма «" + info.Name + "» сохранена.");
                    return;
                }
                computation = CartogramService.Build(document, info.Id, settings, step == Step.Volumes);
                Report(document, computation, step == Step.Volumes);
            }, () =>
            {
                if (computation != null) lastComputation = computation;
                panel.AfterCartogramChange(info.Id);
            });
        }

        private static void Report(Document document, CartogramComputation computation, bool volumes)
        {
            if (!volumes)
            {
                var r = computation.Result;
                string text = "сетка и отметки нанесены: узлов " + r.Nodes.Values.Count(n => n.Used && n.Inside && n.Working.HasValue) +
                              ", точек на границе " + r.BoundaryPoints.Count(b => b.Working.HasValue) + ". Проверьте отметки и нажмите «2. Рассчитать объёмы».";
                document.Editor.WriteMessage("\nКЗМ: «" + computation.Info.Name + "»: " + text);
                foreach (var line in computation.Applied) document.Editor.WriteMessage("\n  " + line);
                foreach (var problem in computation.Problems) document.Editor.WriteMessage("\n  замечание: " + problem);
                PanelMessages.Info("«" + computation.Info.Name + "»: " + text);
                return;
            }
            var lines = CartogramService.Summary(computation);
            document.Editor.WriteMessage("\nКЗМ: картограмма «" + computation.Info.Name + "»:");
            foreach (var line in lines) document.Editor.WriteMessage("\n  " + line);
            foreach (var problem in computation.Problems) document.Editor.WriteMessage("\n  замечание: " + problem);
            PanelMessages.Info("«" + computation.Info.Name + "»: " + lines[0] + (computation.Problems.Count + computation.Marks.Count > 0
                ? " Замечаний: " + (computation.Problems.Count + computation.Marks.Count) + " — см. «Итоги»." : string.Empty));
        }

        /// <summary>«Проверить»: расчёт с текущими значениями формы, чертёж не меняется.</summary>
        private void Check()
        {
            if (panel?.Document == null || !TryRead(out var info)) return;
            try
            {
                using (panel.Document.LockDocument(DocumentLockMode.Read, null, null, false))
                using (var transaction = panel.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    lastComputation = CartogramService.Compute(panel.Document.Database, transaction, info, panel.Snapshot.Settings);
                    transaction.Commit();
                }
                ShowResults(info, false);
                status.Text = "Проверка (чертёж не изменён):\n" + string.Join("\n", CartogramService.Summary(lastComputation)) +
                              (cartogram != null && cartogram.LabelEdits > 0
                                  ? "\nПравки отметок на картограмме (" + cartogram.LabelEdits + ") в проверке не учтены — они войдут при «2. Рассчитать объёмы»." : string.Empty);
                PanelMessages.Info("Проверка выполнена: " + CartogramService.Summary(lastComputation)[0]);
            }
            catch (KzmException exception)
            {
                status.Text = exception.Message;
                status.ForeColor = Color.Firebrick;
            }
        }

        /// <summary>Build or rebuild with the stored parameters (context menu of the tree).</summary>
        internal void Build(KzmPanel owner, string cartogramId, bool volumes)
        {
            if (owner?.Document == null || cartogramId == null) return;
            var settings = owner.Snapshot.Settings;
            CartogramComputation computation = null;
            PanelActions.Run(volumes ? "объёмы картограммы" : "сетка и отметки", document =>
            {
                computation = CartogramService.Build(document, cartogramId, settings, volumes);
                Report(document, computation, volumes);
            }, () =>
            {
                if (computation != null) lastComputation = computation;
                owner.AfterCartogramChange(cartogramId);
            });
        }

        /// <summary>«В Excel…»: узлы, квадраты, итоги, контрольный расчёт (задание п. 6). CSV — по выбору в окне сохранения.</summary>
        internal void Export(KzmPanel owner, string cartogramId)
        {
            var c = owner?.Snapshot.FindCartogram(cartogramId);
            if (c == null || owner.Document == null) { PanelMessages.Error("сначала сохраните картограмму."); return; }
            string folder = null;
            try { folder = Path.GetDirectoryName(owner.Document.Name); } catch (ArgumentException) { }
            string file = new string(c.Info.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            using (var dialog = new SaveFileDialog
            {
                Title = "Выгрузка картограммы «" + c.Info.Name + "»",
                Filter = "Книга Excel (*.xlsx)|*.xlsx|CSV для Excel, разделитель «;» (*.csv)|*.csv",
                FileName = file + ".xlsx",
                InitialDirectory = folder != null && Directory.Exists(folder) ? folder : null,
                OverwritePrompt = true,
            })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var computation = CartogramService.Export(owner.Document, c.Info.Id, dialog.FileName, owner.Snapshot.Settings);
                    PanelMessages.Info("Выгружено: " + dialog.FileName + " (насыпь " + CartogramService.Volume(computation.Result.TotalFill, c.Info.VolumeDecimals, true) +
                                       ", выемка " + CartogramService.Volume(computation.Result.TotalCut, c.Info.VolumeDecimals, false) + " м³).");
                }
                catch (Exception exception) when (exception is KzmException || exception is IOException || exception is UnauthorizedAccessException)
                {
                    PanelMessages.Error("выгрузка не выполнена: " + exception.Message);
                }
            }
        }

        internal void Statement(KzmPanel owner, string cartogramId)
        {
            if (owner == null || cartogramId == null) { status.Text = "Сначала сохраните и постройте картограмму."; return; }
            var settings = owner.Snapshot.Settings;
            PanelActions.RunInteractive("ведомость объёмов", document =>
            {
                CartogramService.InsertStatement(document, cartogramId, settings);
                PanelMessages.Info("Ведомость объёмов земляных масс вставлена (форма рис. 9 СТБ 2073-2010). Строки, кроме «Грунт планировки территории», " +
                                   "заполняете вы; при пересчёте модуль обновит только эту строку.");
            });
        }

        /// <summary>«Удалить картограмму»: целиком — построение, ведомости, граница по точкам и параметры.</summary>
        internal void Delete(KzmPanel owner, string cartogramId)
        {
            var c = owner?.Snapshot.FindCartogram(cartogramId);
            if (c == null || owner.Document == null) return;
            int statements;
            int boundary;
            using (owner.Document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = owner.Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                (statements, boundary) = CartogramService.Extras(owner.Document.Database, transaction, c.Info);
                transaction.Commit();
            }
            string details = "Будут удалены: сетка, отметки, объёмы, штриховка, линия нулевых работ, таблица объёмов, площади, параметры картограммы" +
                             (statements > 0 ? ", ведомость объёмов (" + statements + ")" : string.Empty) +
                             (boundary == 1 ? ", граница, нарисованная по точкам" : boundary > 1 ? ", границы, нарисованные по точкам (" + boundary + ")" : string.Empty) + ".\n" +
                             "Поверхности и их точки не меняются.";
            if (MessageBox.Show(owner, "Удалить картограмму «" + c.Info.Name + "» целиком?\n\n" + details + "\n\nОтменить можно командой U.", "КЗМ",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string id = c.Info.Id, name = c.Info.Name;
            PanelActions.Run("удаление картограммы", document =>
            {
                CartogramService.Delete(document.Database, id);
                PanelMessages.Info("Картограмма «" + name + "» удалена целиком. Отменить — U.");
            }, () => owner.AfterCartogramChange(null));
        }

        private void EraseDrawing()
        {
            if (cartogram == null) return;
            string id = cartogram.Info.Id;
            PanelActions.Run("стереть картограмму", document =>
            {
                CartogramService.EraseDrawing(document.Database, id);
                PanelMessages.Info("Построение картограммы стёрто; параметры и ведомости сохранены.");
            }, () => panel.AfterCartogramChange(id));
        }

        private void MakeTemplate()
        {
            if (panel == null || !TryRead(out var info)) return;
            try
            {
                KzmDefaults.Save(panel.Snapshot.Settings, info);
                PanelMessages.Info("Шаг, обозначения, масштаб, высоты текста и подписи в узлах этой картограммы — шаблон для новых картограмм (" + KzmDefaults.FilePath + ").");
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                PanelMessages.Error("шаблон не записан: " + exception.Message);
            }
        }

        private void ShowDrawing()
        {
            if (cartogram == null || panel?.Document == null) return;
            List<ObjectId> ids;
            using (panel.Document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = panel.Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                ids = CartogramService.ObjectsOf(panel.Document.Database, transaction, cartogram.Info.Id);
                transaction.Commit();
            }
            if (ids.Count == 0) { PanelMessages.Error("картограмма не построена."); return; }
            PanelActions.ShowArea(panel.Document, ids, 20);
        }

        /// <summary>
        /// Another scale: heights that are still the default for the old scale become the default for the new one, so that the
        /// texts keep 0,9 in the drawing (замечание 29). Heights the user typed stay.
        /// </summary>
        private void ScaleChanged()
        {
            if (shownScale < 0 || scale.SelectedIndex < 0) return;
            int next = KzmSettings.Scales[scale.SelectedIndex];
            if (next == shownScale) return;
            string before = SettingsView.FormatValue(CartogramInfo.DefaultTextMm(shownScale));
            string after = SettingsView.FormatValue(CartogramInfo.DefaultTextMm(next));
            foreach (var box in new[] { nodeText, volumeText, totalsText })
                if (box.Text.Trim() == before) box.Text = after;
            shownScale = next;
        }

        /// <summary>The list of the parts; <paramref name="note"/> — what just happened (or null).</summary>
        private void ShowBoundaries(string note)
        {
            boundaryList.BeginUpdate();
            boundaryList.Items.Clear();
            var database = panel?.Document?.Database;
            for (int k = 0; k < boundaryHandles.Count; k++)
            {
                string handle = boundaryHandles[k];
                bool found = database != null && !SurfaceModels.FromHandle(database, handle).IsNull;
                boundaryList.Items.Add((k + 1) + ". полилиния " + handle + (found ? string.Empty : " — нет в чертеже (уберите или укажите заново)"));
            }
            boundaryList.EndUpdate();
            boundaryNote.Text = boundaryHandles.Count == 0
                ? "Граница не указана: добавьте замкнутую полилинию (или несколько) — контур планировки."
                : (boundaryHandles.Count == 1 ? "Граница: одна часть." : "Граница из " + boundaryHandles.Count + " частей — одна общая картограмма.") +
                  (note != null ? " " + note : string.Empty);
        }

        /// <summary>Changes of the list are saved at once for a saved cartogram (a polyline drawn by points is already in the drawing).</summary>
        private void BoundariesChanged(string note)
        {
            ShowBoundaries(note);
            if (newMode || cartogram == null || panel?.Document == null) return;
            string id = cartogram.Info.Id;
            var handles = boundaryHandles.ToList();
            PanelActions.Run("граница картограммы", document => CartogramService.SetBoundaries(document.Database, id, handles), panel.RefreshAll);
        }

        private void ShowBoundary()
        {
            if (panel?.Document == null || boundaryHandles.Count == 0) return;
            var handles = boundaryList.SelectedIndex >= 0 && boundaryList.SelectedIndex < boundaryHandles.Count
                ? new[] { boundaryHandles[boundaryList.SelectedIndex] } : boundaryHandles.ToArray();
            var ids = handles.Select(h => SurfaceModels.FromHandle(panel.Document.Database, h)).Where(id => !id.IsNull).ToList();
            if (ids.Count == 0) { PanelMessages.Error("полилиния границы не найдена в чертеже."); return; }
            PanelActions.Show(panel.Document, ids, 20);
        }

        /// <summary>
        /// «Убрать»: the part leaves the list. A part drawn by points for this cartogram is erased with its structure line
        /// (its points stay in the project surface); a user's polyline stays in the drawing.
        /// </summary>
        private void RemoveBoundary()
        {
            int index = boundaryList.SelectedIndex;
            if (index < 0 || index >= boundaryHandles.Count) { PanelMessages.Error("выберите в списке часть границы, которую убрать."); return; }
            string handle = boundaryHandles[index];
            var remaining = boundaryHandles.Where((h, k) => k != index).ToList();
            if (panel?.Document == null)
            {
                boundaryHandles = remaining;
                ShowBoundaries(null);
                return;
            }
            string id = newMode ? draftId : cartogram?.Info.Id;
            bool save = !newMode && cartogram != null;
            string projectId = (project.SelectedItem as SurfaceItem)?.Info.Id;
            var settings = panel.Snapshot.Settings;
            bool erased = false;
            // One command, one U: the polyline drawn by points (if it is one) and the saved list.
            PanelActions.Run("граница картограммы", document =>
            {
                erased = id != null && SurfaceTools.RemoveBoundary(document.Database, id, projectId, handle, settings);
                if (save) CartogramService.SetBoundaries(document.Database, id, remaining);
            }, () =>
            {
                boundaryHandles = remaining;
                ShowBoundaries(erased ? "Часть, нарисованная по точкам, стёрта вместе со структурной линией; её точки остались в проектной поверхности."
                                      : "Полилиния осталась в чертеже, в расчёт она больше не входит.");
                panel?.RefreshAll();
            });
        }

        private void ShowProblem()
        {
            if (!(problems.SelectedItem is ProblemItem item) || !item.At.HasValue || panel?.Document == null) return;
            double size = Math.Max(10, (lastComputation?.Info.Step ?? 20) * 2);
            PanelActions.ShowPoint(panel.Document, item.At.Value, size);
        }

        // ---------- temporary grid preview (not saved, nothing changes in the drawing) ----------

        private void TogglePreview()
        {
            if (previewEntities.Count > 0) { ClearPreview(); return; }
            if (panel?.Document == null) return;
            var errors = new List<string>();
            var info = Read(errors);
            errors = errors.Where(e => e.Contains("шаг") || e.Contains("базов") || e.Contains("угол") || e.Contains("границ")).ToList();
            if (boundaryHandles.Count == 0) errors.Add("укажите границу работ");
            if (errors.Count > 0) { PanelMessages.Error(string.Join("; ", errors.Distinct()) + "."); return; }
            try
            {
                List<List<XY>> boundaries;
                using (panel.Document.LockDocument(DocumentLockMode.Read, null, null, false))
                using (var transaction = panel.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    boundaries = SurfaceModels.ReadOutlines(panel.Document.Database, transaction, BoundaryList.Join(boundaryHandles), "Граница работ");
                    transaction.Commit();
                }
                var all = boundaries.SelectMany(b => b).ToList();
                var grid = CartogramService.GridFor(info, all);
                var (iMin, iMax, jMin, jMax) = CartogramCalculator.Range(all, grid);
                if ((long)(iMax - iMin + 2) + (jMax - jMin + 2) > 20000) { PanelMessages.Error("слишком много линий сетки — проверьте шаг."); return; }
                // As it will be drawn: only inside the parts of the boundary.
                foreach (var (a, b) in CartogramCalculator.GridSegments(boundaries, grid))
                    previewEntities.Add(new Line(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0)) { ColorIndex = 3 });
                var manager = TransientManager.CurrentTransientManager;
                foreach (var entity in previewEntities) manager.AddTransient(entity, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection());
                preview.Text = "Скрыть сетку";
                PanelMessages.Info("Сетка показана временно (" + (iMax - iMin + 1) + " × " + (jMax - jMin + 1) + " квадратов, базовая точка " + grid.Origin +
                                   "). Измените шаг, базовую точку или угол и покажите снова.");
                try { Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView(); } catch (System.Exception) { }
                panel.Document.Editor.UpdateScreen();
            }
            catch (KzmException exception)
            {
                ClearPreview();
                PanelMessages.Error(exception.Message);
            }
        }

        internal void ClearPreview()
        {
            if (previewEntities.Count == 0) return;
            var manager = TransientManager.CurrentTransientManager;
            foreach (var entity in previewEntities)
            {
                try { manager.EraseTransient(entity, new IntegerCollection()); } catch (System.Exception) { }
                entity.Dispose();
            }
            previewEntities.Clear();
            preview.Text = "Показать сетку";
            try { panel?.Document?.Editor.UpdateScreen(); } catch (System.Exception) { }
        }

        /// <summary>«Указать границу»: how — by points with elevations from a surface, or an existing polyline.</summary>
        private ContextMenuStrip BoundaryMenu()
        {
            var menu = new ContextMenuStrip();
            var factItem = fact.SelectedItem as SurfaceItem;
            var projectItem = project.SelectedItem as SurfaceItem;
            void Item(string text, Action action, bool enabled = true)
            {
                var item = new ToolStripMenuItem(text) { Enabled = enabled };
                item.Click += (s, e) => action();
                menu.Items.Add(item);
            }
            Item(factItem != null ? "По точкам — отметки с фактической «" + factItem.Info.Name + "»…" : "По точкам — отметки с фактической (не выбрана)",
                () => DrawBoundary(factItem.Info.Id), factItem != null && projectItem != null);
            Item(projectItem != null ? "По точкам — отметки с проектной «" + projectItem.Info.Name + "»…" : "По точкам — отметки с проектной (не выбрана)",
                () => DrawBoundary(projectItem.Info.Id), projectItem != null);
            menu.Items.Add(new ToolStripSeparator());
            Item("Выбрать готовые полилинии (одну или несколько)…", PickBoundary);
            return menu;
        }

        private void DrawBoundary(string sourceSurfaceId)
        {
            if (panel?.Document == null) return;
            var projectItem = project.SelectedItem as SurfaceItem;
            if (projectItem == null) { PanelMessages.Error("сначала выберите проектную поверхность: в неё войдут точки границы."); return; }
            var errors = new List<string>();
            var info = Read(errors);
            // A new part is added to the list (замечание 31): nothing drawn before is replaced.
            string id = info.Id, name = string.IsNullOrWhiteSpace(info.Name) ? "Картограмма" : info.Name;
            string target = projectItem.Info.Id;
            var settings = panel.Snapshot.Settings;
            bool save = !newMode && cartogram != null;
            var current = boundaryHandles.ToList();
            string handle = null;
            PanelActions.RunInteractive("граница по точкам", document =>
            {
                handle = SurfaceTools.DrawBoundary(document, id, name, target, sourceSurfaceId, null, settings);
                if (handle != null && save) CartogramService.SetBoundaries(document.Database, id, current.Concat(new[] { handle }).ToList());
            }, () =>
            {
                if (handle == null) return;
                if (!boundaryHandles.Contains(handle, StringComparer.OrdinalIgnoreCase)) boundaryHandles.Add(handle);
                PanelMessages.Info("Граница нарисована: точки и структурная линия добавлены в проектную поверхность. Дальше — «1. Сетка и отметки».");
                ShowBoundaries("Добавлена часть, нарисованная по точкам.");
                panel.RefreshAll();
            });
        }

        /// <summary>One or several closed polylines are added to the list of parts.</summary>
        private void PickBoundary()
        {
            string handles = null;
            PanelActions.Pick("граница работ", document =>
            {
                handles = BoundaryPicker.Pick(document, "\nВыберите одну или несколько замкнутых полилиний — части границы работ (Enter — готово): ");
            }, () =>
            {
                var added = BoundaryList.Split(handles).Where(h => !boundaryHandles.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
                if (added.Count == 0) return;
                boundaryHandles.AddRange(added);
                BoundariesChanged(added.Count == 1 ? "Добавлена полилиния " + added[0] + "." : "Добавлено полилиний: " + added.Count + ".");
            });
        }

        private void PickOrigin()
        {
            Point3d? picked = null;
            PanelActions.Pick("базовая точка сетки", document =>
            {
                var result = document.Editor.GetPoint(new PromptPointOptions("\nУкажите базовую точку сетки (узел 0,0): "));
                if (result.Status == PromptStatus.OK) picked = result.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            }, () =>
            {
                if (!picked.HasValue) return;
                originPoint.Checked = true;
                originX.Text = SettingsView.FormatValue(Math.Round(picked.Value.X, 3));
                originY.Text = SettingsView.FormatValue(Math.Round(picked.Value.Y, 3));
            });
        }

    }
}
