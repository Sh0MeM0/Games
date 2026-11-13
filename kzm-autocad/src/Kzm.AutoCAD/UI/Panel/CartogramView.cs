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
        private readonly ComboBox fact = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly ComboBox project = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        private readonly Label boundaryNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly TextBox step = new TextBox { Width = 70 };
        private readonly RadioButton originAuto = new RadioButton { Text = "Авто (кратно шагу)", AutoSize = true };
        private readonly RadioButton originPoint = new RadioButton { Text = "Точка", AutoSize = true };
        private readonly TextBox originX = new TextBox { Width = 110 };
        private readonly TextBox originY = new TextBox { Width = 110 };
        private readonly TextBox angle = new TextBox { Width = 70 };
        private readonly ComboBox columns = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ComboBox rows = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ComboBox scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        private readonly TextBox nodeText = new TextBox { Width = 60 };
        private readonly TextBox volumeText = new TextBox { Width = 60 };
        private readonly TextBox totalsText = new TextBox { Width = 60 };
        private readonly NumericUpDown volumeDecimals = new NumericUpDown { Minimum = 0, Maximum = 3, Width = 60 };
        private readonly ComboBox[] corners = new ComboBox[3];
        private readonly Button[] colors = new Button[3];
        private readonly ComboBox[] layers = new ComboBox[3];
        private readonly string[] colorValues = { PointAppearance.ByLayer, PointAppearance.ByLayer, PointAppearance.ByLayer };
        private readonly CheckBox control = new CheckBox { Text = "Контрольный расчёт по наложению поверхностей (п. 5)", AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly CheckBox keepMoved = new CheckBox { Text = "При пересчёте ставить сдвинутые вручную подписи на их место (п. 7)", AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly ListBox problems = new ListBox { Width = 330, Height = 90, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Button build = new Button { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private readonly Button preview = new Button { Text = "Показать сетку", AutoSize = true };
        private readonly List<Entity> previewEntities = new List<Entity>();
        private KzmPanel panel;
        private CartogramSummary cartogram;
        private bool creating;
        private bool newMode;
        private string boundaryHandle;
        private string lastBound;
        private CartogramComputation lastComputation;

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
            Span(Flow(Action("Указать границу…", (s, e) => PickBoundary()), Action("Показать", (s, e) => ShowBoundary())));

            Span(Section("Сетка квадратов"));
            Row("Шаг, м", step);
            Row("Базовая точка", Flow(originAuto, originPoint));
            Row("X / Y", Flow(originX, originY, Action("Указать…", (s, e) => PickOrigin())));
            Row("Угол поворота, °", Flow(angle, Action("По двум точкам…", (s, e) => PickAngle())));
            foreach (var box in new[] { columns, rows }) { box.Items.Add("1, 2, 3 …"); box.Items.Add("А, Б, В …"); }
            Row("Столбцы", columns);
            Row("Ряды (сверху вниз)", rows);
            preview.Click += (s, e) => TogglePreview();
            Span(preview);

            Span(Section("Оформление (СТБ 2073-2010)"));
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            Row("Масштаб", scale);
            Row("Текст отметок в узлах, мм", nodeText);
            Row("Текст объёмов, мм", volumeText);
            Row("Текст итогов, мм", totalsText);
            Row("Объёмы: знаков после запятой", volumeDecimals);

            Span(Section("Подписи в узле"));
            string[] titles = { "Рабочая (h)", "Проектная (Hпр)", "Фактическая (Hф)" };
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                corners[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
                foreach (LabelCorner corner in Enum.GetValues(typeof(LabelCorner))) corners[i].Items.Add(NodeLabelStyle.Title(corner));
                colors[i] = new Button { AutoSize = true, Text = "ПоСлою" };
                colors[i].Click += (s, e) => PickColor(index);
                layers[i] = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 150 };
                Row(titles[i], Flow(corners[i], colors[i], layers[i]));
            }
            Span(Hint("По умолчанию — как в примере Б.4 СТБ 2073-2010: рабочая — слева вверху, проектная — справа вверху, фактическая — справа внизу. " +
                      "Линия нулевых работ — штриховая тонкая (п. 3.12), выемки — штриховка 45° (п. 7.1). Под сеткой — таблица по рис. 8, справа — итоги по рядам. " +
                      "Высоты текста — из ряда ГОСТ 2.304, их можно менять."));

            Span(Section("Расчёт"));
            Span(control);
            Span(keepMoved);

            build.Click += (s, e) => Save(true);
            Span(Flow(build, Action("Проверить", (s, e) => Check()), Action("Сохранить", (s, e) => Save(false))));
            Span(Flow(Action("Показать на чертеже", (s, e) => ShowDrawing()), Action("В Excel…", (s, e) => Export(panel, cartogram?.Info.Id)),
                Action("Ведомость объёмов…", (s, e) => Statement(panel, cartogram?.Info.Id))));
            Span(Flow(Action("Стереть построение", (s, e) => EraseDrawing()), Action("Удалить…", (s, e) => Delete(panel, cartogram?.Info.Id)),
                Action("Сделать шаблоном", (s, e) => MakeTemplate())));
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
            fact.Items.Clear();
            project.Items.Clear();
            foreach (var s in panel.Snapshot.Surfaces)
                (s.Info.Kind == SurfaceKind.Project ? project : fact).Items.Add(new SurfaceItem(s.Info));
            // Either kind may be used on either side if the user wants it (e.g. two project variants).
            foreach (var s in panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Project)) fact.Items.Add(new SurfaceItem(s.Info, true));
            foreach (var s in panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Fact)) project.Items.Add(new SurfaceItem(s.Info, true));
            var layerNames = panel.Document != null ? DrawingData.Layers(panel.Document) : new List<string>();
            foreach (var box in layers)
            {
                string typed = box.Text;
                box.Items.Clear();
                foreach (var layer in new[] { NodeLabelStyle.WorkingLayer, NodeLabelStyle.ProjectLayer, NodeLabelStyle.FactLayer }.Concat(layerNames)
                             .Distinct(StringComparer.OrdinalIgnoreCase)) box.Items.Add(layer);
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
                boundaryHandle = null;
                lastComputation = null;
                var info = new CartogramInfo
                {
                    Name = "Картограмма " + (panel.Snapshot.Cartograms.Count + 1),
                    Scale = panel.Snapshot.Settings.Scale,
                    FactSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Fact)?.Info.Id,
                    ProjectSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Project)?.Info.Id,
                };
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
                boundaryHandle = cartogram.Info.BoundaryHandle;
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
            boundaryNote.Text = string.IsNullOrEmpty(boundaryHandle)
                ? "Граница не указана: укажите замкнутую полилинию — контур планировки."
                : "Граница: полилиния " + boundaryHandle + ".";
            step.Text = SettingsView.FormatValue(info.Step);
            originAuto.Checked = !(info.OriginX.HasValue && info.OriginY.HasValue);
            originPoint.Checked = !originAuto.Checked;
            originX.Text = SettingsView.FormatValue(info.OriginX);
            originY.Text = SettingsView.FormatValue(info.OriginY);
            originX.Enabled = originY.Enabled = originPoint.Checked;
            angle.Text = SettingsView.FormatValue(info.AngleDegrees);
            columns.SelectedIndex = (int)info.Columns;
            rows.SelectedIndex = (int)info.Rows;
            scale.SelectedIndex = Math.Max(0, Array.IndexOf(KzmSettings.Scales, info.Scale));
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
            build.Text = isNew || !info.Built ? "Построить" : "Пересчитать";
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
            else if (isNew) status.Text = "Заполните поля и нажмите «Проверить» (чертёж не меняется) или «Построить».";
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
            if (outdated) status.Text += "\nУСТАРЕЛА: точки, структурные линии, границы или параметры изменились — нажмите «Пересчитать».";
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
            var info = cartogram?.Info.Clone() ?? new CartogramInfo { Id = Guid.NewGuid().ToString("N") };
            info.Name = name.Text.Trim();
            info.FactSurfaceId = (fact.SelectedItem as SurfaceItem)?.Info.Id;
            info.ProjectSurfaceId = (project.SelectedItem as SurfaceItem)?.Info.Id;
            info.BoundaryHandle = boundaryHandle;
            info.Step = SettingsView.Parse(step.Text, "шаг", errors) ?? 0;
            if (originPoint.Checked)
            {
                info.OriginX = SettingsView.Parse(originX.Text, "X базовой точки", errors);
                info.OriginY = SettingsView.Parse(originY.Text, "Y базовой точки", errors);
                if (!info.OriginX.HasValue || !info.OriginY.HasValue) errors.Add("укажите базовую точку или выберите «Авто»");
            }
            else info.OriginX = info.OriginY = null;
            info.AngleDegrees = SettingsView.Parse(angle.Text, "угол", errors) ?? 0;
            info.Columns = (LabelScheme)Math.Max(0, columns.SelectedIndex);
            info.Rows = (LabelScheme)Math.Max(0, rows.SelectedIndex);
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

        private void Save(bool andBuild)
        {
            if (panel?.Document == null || !TryRead(out var info)) return;
            ClearPreview();
            var settings = panel.Snapshot.Settings;
            CartogramComputation computation = null;
            PanelActions.Run(andBuild ? "картограмма" : "сохранение картограммы", document =>
            {
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    KzmStore.WriteCartogram(document.Database, transaction, info);
                    transaction.Commit();
                }
                if (!andBuild)
                {
                    PanelMessages.Info("Картограмма «" + info.Name + "» сохранена.");
                    return;
                }
                computation = CartogramService.Build(document, info.Id, settings);
                Report(document, computation);
            }, () =>
            {
                if (computation != null) lastComputation = computation;
                panel.AfterCartogramChange(info.Id);
            });
        }

        private static void Report(Document document, CartogramComputation computation)
        {
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
                status.Text = "Проверка (чертёж не изменён):\n" + string.Join("\n", CartogramService.Summary(lastComputation));
                PanelMessages.Info("Проверка выполнена: " + CartogramService.Summary(lastComputation)[0]);
            }
            catch (KzmException exception)
            {
                status.Text = exception.Message;
                status.ForeColor = Color.Firebrick;
            }
        }

        /// <summary>Build or rebuild with the stored parameters (context menu of the tree).</summary>
        internal void Build(KzmPanel owner, string cartogramId)
        {
            if (owner?.Document == null || cartogramId == null) return;
            var settings = owner.Snapshot.Settings;
            CartogramComputation computation = null;
            PanelActions.Run("картограмма", document =>
            {
                computation = CartogramService.Build(document, cartogramId, settings);
                Report(document, computation);
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
            PanelActions.RunInteractive("ведомость объёмов", document =>
            {
                CartogramService.InsertStatement(document, cartogramId);
                PanelMessages.Info("Ведомость объёмов земляных масс вставлена (форма рис. 9 СТБ 2073-2010). Строки, кроме «Грунт планировки территории», " +
                                   "заполняете вы; при пересчёте модуль обновит только эту строку.");
            });
        }

        internal void Delete(KzmPanel owner, string cartogramId)
        {
            var c = owner?.Snapshot.FindCartogram(cartogramId);
            if (c == null) return;
            if (MessageBox.Show(owner, "Удалить картограмму «" + c.Info.Name + "» вместе с построением?\nВедомости объёмов останутся на чертеже.\nОтменить можно командой U.", "КЗМ",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string id = c.Info.Id;
            PanelActions.Run("удаление картограммы", document =>
            {
                CartogramService.Delete(document.Database, id, true);
                PanelMessages.Info("Картограмма удалена.");
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

        private void ShowBoundary()
        {
            if (panel?.Document == null || boundaryHandle == null) return;
            var id = SurfaceModels.FromHandle(panel.Document.Database, boundaryHandle);
            if (id.IsNull) { PanelMessages.Error("полилиния границы не найдена в чертеже."); return; }
            PanelActions.Show(panel.Document, new[] { id }, 20);
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
            if (string.IsNullOrEmpty(boundaryHandle)) errors.Add("укажите границу работ");
            if (errors.Count > 0) { PanelMessages.Error(string.Join("; ", errors.Distinct()) + "."); return; }
            try
            {
                List<XY> boundary;
                using (panel.Document.LockDocument(DocumentLockMode.Read, null, null, false))
                using (var transaction = panel.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    boundary = SurfaceModels.ReadOutline(panel.Document.Database, transaction, boundaryHandle, "Граница работ");
                    transaction.Commit();
                }
                var grid = CartogramService.GridFor(info, boundary);
                var (iMin, iMax, jMin, jMax) = CartogramCalculator.Range(boundary, grid);
                if ((long)(iMax - iMin + 2) + (jMax - jMin + 2) > 20000) { PanelMessages.Error("слишком много линий сетки — проверьте шаг."); return; }
                double s = grid.Step;
                Point3d P(double u, double v) { var w = grid.ToWorld(u, v); return new Point3d(w.X, w.Y, 0); }
                for (int i = iMin; i <= iMax + 1; i++) previewEntities.Add(new Line(P(i * s, jMin * s), P(i * s, (jMax + 1) * s)) { ColorIndex = 3 });
                for (int j = jMin; j <= jMax + 1; j++) previewEntities.Add(new Line(P(iMin * s, j * s), P((iMax + 1) * s, j * s)) { ColorIndex = 3 });
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

        private void PickBoundary()
        {
            string handle = null;
            PanelActions.Pick("граница работ", document =>
            {
                var options = new PromptEntityOptions("\nУкажите замкнутую полилинию — границу работ (контур картограммы): ");
                options.SetRejectMessage("\nНужна замкнутая полилиния.");
                options.AddAllowedClass(typeof(Curve), false);
                var pick = document.Editor.GetEntity(options);
                if (pick.Status != PromptStatus.OK) return;
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Drawing.Sources.Outline((Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead), transaction);
                    transaction.Commit();
                }
                handle = pick.ObjectId.Handle.ToString();
            }, () =>
            {
                if (handle == null) return;
                boundaryHandle = handle;
                boundaryNote.Text = "Граница: полилиния " + handle + " (сохраните или постройте картограмму).";
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

        private void PickAngle()
        {
            double? degrees = null;
            PanelActions.Pick("угол сетки", document =>
            {
                var first = document.Editor.GetPoint(new PromptPointOptions("\nПервая точка направления осей сетки: "));
                if (first.Status != PromptStatus.OK) return;
                var second = document.Editor.GetPoint(new PromptPointOptions("\nВторая точка: ") { BasePoint = first.Value, UseBasePoint = true });
                if (second.Status != PromptStatus.OK) return;
                var ucs = document.Editor.CurrentUserCoordinateSystem;
                var a = first.Value.TransformBy(ucs);
                var b = second.Value.TransformBy(ucs);
                degrees = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            }, () =>
            {
                if (degrees.HasValue) angle.Text = SettingsView.FormatValue(Math.Round(degrees.Value, 6));
            });
        }
    }
}
