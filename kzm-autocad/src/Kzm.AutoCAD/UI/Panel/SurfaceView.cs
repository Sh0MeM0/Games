using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>Properties of one surface, or the form of a new surface.</summary>
    internal sealed class SurfaceView : UserControl, IPanelView
    {
        private readonly Label title = new Label();
        private readonly TextBox name = new TextBox();
        private readonly RadioButton fact = new RadioButton { Text = "Фактическая", AutoSize = true, Checked = true };
        private readonly RadioButton project = new RadioButton { Text = "Проектная", AutoSize = true };
        private readonly ComboBox layer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckBox movePoints = new CheckBox { Text = "Перенести существующие точки на этот слой", AutoSize = true, Checked = true };
        private readonly Label info = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly Button save = new Button { AutoSize = true };
        private readonly Button makeCurrent = new Button { Text = "Сделать текущей", AutoSize = true };
        private readonly Button import = new Button { Text = "Импорт отметок…", AutoSize = true };
        private readonly Button delete = new Button { Text = "Удалить поверхность…", AutoSize = true };
        private KzmPanel panel;
        private SurfaceSummary surface;
        private bool isNew;

        // Точки, оформление, триангуляция (только для существующей поверхности).
        private readonly TableLayoutPanel details = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(6) };
        private readonly ComboBox textStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly TextBox textHeight = new TextBox { Width = 70 };
        private readonly TextBox numberHeight = new TextBox { Width = 70 };
        private readonly TextBox signSize = new TextBox { Width = 70 };
        private readonly CheckBox showNumbers = new CheckBox { Text = "Показывать номера точек", AutoSize = true };
        private readonly Button elevationColor = new Button { AutoSize = true };
        private readonly Button numberColor = new Button { AutoSize = true };
        private readonly Label appearanceNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText };
        private readonly Label boundaryNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly CheckBox showTriangles = new CheckBox { Text = "Показывать треугольники (слой КЗМ_ТРЕУГ)", AutoSize = true };
        private readonly Label tinNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly Button refreshTriangles = new Button { Text = "Обновить треугольники", AutoSize = true };
        private readonly ListBox lines = new ListBox { Width = 320, Height = 80, IntegralHeight = false };
        private readonly Label linesNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText };
        private string elevationColorValue = PointAppearance.ByLayer;
        private string numberColorValue = PointAppearance.ByLayer;
        private bool bindingDetails;

        internal SurfaceView()
        {
            AutoScroll = true;
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            title.AutoSize = true;
            table.Controls.Add(title, 0, 0);
            table.SetColumnSpan(title, 2);

            AddRow(table, "Имя", name);
            var kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            kinds.Controls.Add(fact);
            kinds.Controls.Add(project);
            AddRow(table, "Тип", kinds);
            AddRow(table, "Слой точек", layer);
            table.Controls.Add(movePoints);
            table.SetColumnSpan(movePoints, 2);
            table.Controls.Add(info);
            table.SetColumnSpan(info, 2);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { save, makeCurrent, import, delete });
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            BuildDetails();
            AutoScroll = false;
            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            table.Dock = DockStyle.None;
            root.Controls.Add(table);
            root.Controls.Add(details);
            Controls.Add(root);

            name.Anchor = layer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            fact.CheckedChanged += (s, e) => SwapDefaultLayer();
            layer.TextChanged += (s, e) => movePoints.Visible = !isNew && surface != null && surface.Points > 0 &&
                                                               !string.Equals(layer.Text.Trim(), surface.Info.Layer, StringComparison.OrdinalIgnoreCase);
            save.Click += (s, e) => Save();
            makeCurrent.Click += (s, e) => panel.SetCurrent(surface?.Info.Id);
            import.Click += (s, e) => panel.ShowImport(surface?.Info.Id);
            delete.Click += (s, e) => Delete();
        }

        private static void AddRow(TableLayoutPanel table, string caption, Control control)
        {
            table.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) });
            table.Controls.Add(control);
        }

        private SurfaceKind newKind = SurfaceKind.Fact;

        internal void PrepareNew(SurfaceKind? kind = null)
        {
            isNew = true;
            newKind = kind ?? SurfaceKind.Fact;
        }

        private static Label Section(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };

        private void Span(Control control)
        {
            details.Controls.Add(control);
            details.SetColumnSpan(control, 2);
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(340, 0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        private void BuildDetails()
        {
            details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Span(Section("Точки"));
            var importButton = new Button { Text = "Импорт из текстов…", AutoSize = true };
            importButton.Click += (s, e) => panel.ShowImport(surface?.Info.Id);
            var manual = new Button { Text = "Создать вручную…", AutoSize = true };
            manual.Click += (s, e) => CreateByHand();
            var table = new Button { Text = "Таблица точек", AutoSize = true };
            table.Click += (s, e) => panel.ShowPoints(surface?.Info.Id);
            var plane = new Button { Text = "Плоскость…", AutoSize = true };
            plane.Click += (s, e) => panel.ShowPlane(surface?.Info.Id);
            Span(Flow(importButton, manual, table, plane));
            Span(new Label
            {
                AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText,
                Text = "Вручную: укажите точку на чертеже (работают привязки), введите отметку в командной строке; Enter — повторить предыдущую; Esc — закончить.",
            });

            Span(Section("Оформление подписей"));
            AddDetail("Шрифт (стиль текста)", textStyle);
            AddDetail("Высота отметки, м", textHeight);
            AddDetail("Высота номера, м", numberHeight);
            AddDetail("Размер знака, м", signSize);
            AddDetail("Цвет отметок", elevationColor);
            AddDetail("Цвет номеров", numberColor);
            Span(showNumbers);
            var sample = new Button { Text = "Взять с образца…", AutoSize = true };
            sample.Click += (s, e) => TakeFromSample();
            var apply = new Button { Text = "Применить ко всем точкам", AutoSize = true };
            apply.Click += (s, e) => SaveAppearance(true);
            var saveOnly = new Button { Text = "Сохранить для новых", AutoSize = true };
            saveOnly.Click += (s, e) => SaveAppearance(false);
            Span(Flow(sample, apply, saveOnly));
            Span(appearanceNote);
            elevationColor.Click += (s, e) => PickColor(ref elevationColorValue, elevationColor);
            numberColor.Click += (s, e) => PickColor(ref numberColorValue, numberColor);

            Span(Section("Структурные линии"));
            Span(lines);
            var addLine = new Button { Text = "Добавить по полилинии…", AutoSize = true };
            addLine.Click += (s, e) => LineView.PickPolyline(panel, surface?.Info.Id);
            var showLine = new Button { Text = "Показать", AutoSize = true };
            showLine.Click += (s, e) => ShowLine();
            var deleteLine = new Button { Text = "Удалить…", AutoSize = true };
            deleteLine.Click += (s, e) => DeleteLine();
            Span(Flow(addLine, showLine, deleteLine));
            Span(linesNote);
            lines.DoubleClick += (s, e) => ShowLine();

            Span(Section("Поверхность (триангуляция)"));
            Span(boundaryNote);
            var pickBoundary = new Button { Text = "Указать границу…", AutoSize = true };
            pickBoundary.Click += (s, e) => PickBoundary();
            var clearBoundary = new Button { Text = "Без границы", AutoSize = true };
            clearBoundary.Click += (s, e) => SetBoundary(null);
            Span(Flow(pickBoundary, clearBoundary));
            Span(showTriangles);
            showTriangles.CheckedChanged += (s, e) => { if (!bindingDetails) ShowTriangles(showTriangles.Checked); };
            refreshTriangles.Click += (s, e) => ShowTriangles(true);
            var check = new Button { Text = "Проверить триангуляцию", AutoSize = true };
            check.Click += (s, e) => CheckTin();
            var flip = new Button { Text = "Поменять диагональ…", AutoSize = true };
            flip.Click += (s, e) => FlipDiagonals();
            Span(Flow(refreshTriangles, check, flip));
            Span(tinNote);
        }

        private void AddDetail(string caption, Control control)
        {
            details.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(160, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 6, 3) });
            details.Controls.Add(control);
        }

        private void BindDetails()
        {
            details.Visible = surface != null && !creating;
            if (!details.Visible) return;
            bindingDetails = true;
            try
            {
                var appearance = surface.Info.Appearance;
                textStyle.Items.Clear();
                if (panel.Document != null)
                    foreach (var style in panel.Snapshot.TextStyles.Value) textStyle.Items.Add(style);
                textStyle.SelectedItem = appearance.TextStyle != null && textStyle.Items.Contains(appearance.TextStyle) ? appearance.TextStyle : null;
                textHeight.Text = SettingsView.FormatValue(appearance.TextHeight);
                numberHeight.Text = SettingsView.FormatValue(appearance.NumberHeight);
                signSize.Text = SettingsView.FormatValue(appearance.SignSize);
                showNumbers.Checked = appearance.ShowNumber;
                elevationColorValue = appearance.ElevationColor ?? PointAppearance.ByLayer;
                numberColorValue = appearance.NumberColor ?? PointAppearance.ByLayer;
                ShowColor(elevationColor, elevationColorValue);
                ShowColor(numberColor, numberColorValue);
                appearanceNote.Text = appearance.IsSet
                    ? "Пустые поля: высота номера = высоте отметки, знак = половине высоты отметки."
                    : "Оформление ещё не задано: оно возьмётся с текстов при импорте или с образца («Взять с образца»).";
                boundaryNote.Text = string.IsNullOrEmpty(surface.Info.BoundaryHandle)
                    ? "Граница поверхности не задана: треугольники строятся по всем точкам (выпуклая оболочка)."
                    : "Граница поверхности: полилиния " + surface.Info.BoundaryHandle + " — снаружи отметок нет, треугольники обрезаются по ней.";
                lines.Items.Clear();
                foreach (var line in surface.Lines) lines.Items.Add(new LineItem(line));
                linesNote.Text = surface.Lines.Count == 0
                    ? "Структурная линия (бровка, бортовой камень, лоток) — рёбра треугольников идут по ней, отметки между её точками — по длине линии."
                    : "Линий: " + surface.StructureLines + (surface.Flips > 0 ? ", заданных диагоналей: " + surface.Flips : string.Empty) +
                      ". Красные рёбра треугольников — структурные линии. Двойной щелчок — показать.";
                showTriangles.Checked = surface.Info.ShowTriangles;
                refreshTriangles.Visible = surface.Info.ShowTriangles && surface.TrianglesOutdated;
                tinNote.Text = surface.TrianglesOutdated
                    ? "Точки изменились после построения треугольников — нажмите «Обновить треугольники». Картограмма всегда считается по текущим точкам."
                    : "Отметка в любой точке — линейная интерполяция внутри треугольника; вне поверхности — «нет данных».";
            }
            finally
            {
                bindingDetails = false;
            }
        }

        private static void ShowColor(Button button, string value)
        {
            var color = PointBlocks.ParseColor(value);
            button.Text = color.IsByLayer ? "ПоСлою" : color.IsByAci ? "Индекс " + color.ColorIndex : "RGB " + color.Red + "," + color.Green + "," + color.Blue;
            button.BackColor = color.IsByLayer ? SystemColors.Control : color.ColorValue;
            button.ForeColor = color.IsByLayer || color.ColorValue.GetBrightness() > 0.5 ? Color.Black : Color.White;
        }

        private void PickColor(ref string value, Button button)
        {
            var dialog = new Autodesk.AutoCAD.Windows.ColorDialog
            {
                Color = PointBlocks.ParseColor(value),
                IncludeByBlockByLayer = true,
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            value = PointBlocks.FormatColor(dialog.Color);
            ShowColor(button, value);
        }

        private PointAppearance ReadAppearance(List<string> errors)
        {
            var appearance = surface.Info.Appearance.Clone();
            appearance.TextStyle = textStyle.SelectedItem as string ?? appearance.TextStyle;
            appearance.TextHeight = SettingsView.Parse(textHeight.Text, "высота отметки", errors);
            appearance.NumberHeight = SettingsView.Parse(numberHeight.Text, "высота номера", errors);
            appearance.SignSize = SettingsView.Parse(signSize.Text, "размер знака", errors);
            appearance.ShowNumber = showNumbers.Checked;
            appearance.ElevationColor = elevationColorValue;
            appearance.NumberColor = numberColorValue;
            if (!appearance.TextHeight.HasValue) errors.Add("задайте высоту отметки или возьмите её с образца");
            foreach (var value in new[] { appearance.TextHeight, appearance.NumberHeight, appearance.SignSize })
                if (value.HasValue && !(value.Value > 0)) errors.Add("размеры должны быть больше нуля");
            return appearance;
        }

        private void SaveAppearance(bool applyToPoints)
        {
            if (surface == null) return;
            var errors = new List<string>();
            var appearance = ReadAppearance(errors);
            if (errors.Count > 0) { PanelMessages.Error(string.Join("; ", errors.Distinct()) + "."); return; }
            string id = surface.Info.Id;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("оформление точек", document =>
            {
                int count = EditServices.SaveAppearance(document.Database, id, appearance, applyToPoints, settings);
                PanelMessages.Info(applyToPoints ? "Оформление применено к точкам: " + count + "." : "Оформление сохранено для новых точек.");
            }, panel.RefreshAll);
        }

        /// <summary>Шрифт и высота — с указанного текста на чертеже (цвета не меняются).</summary>
        private void TakeFromSample()
        {
            if (surface == null) return;
            PointAppearance taken = null;
            var current = surface.Info.Appearance;
            PanelActions.Pick("образец оформления", document =>
            {
                var options = new PromptEntityOptions("\nУкажите текст-образец (шрифт и высота подписи): ");
                options.SetRejectMessage("\nНужен TEXT или MTEXT.");
                options.AddAllowedClass(typeof(DBText), true);
                options.AddAllowedClass(typeof(MText), true);
                var pick = document.Editor.GetEntity(options);
                if (pick.Status != PromptStatus.OK) return;
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    taken = EditServices.FromSample((Entity)transaction.GetObject(pick.ObjectId, OpenMode.ForRead), current);
                    transaction.Commit();
                }
            }, () =>
            {
                if (taken == null) return;
                if (!textStyle.Items.Contains(taken.TextStyle)) textStyle.Items.Add(taken.TextStyle);
                textStyle.SelectedItem = taken.TextStyle;
                textHeight.Text = SettingsView.FormatValue(taken.TextHeight);
                numberHeight.Text = string.Empty;
                signSize.Text = string.Empty;
                PanelMessages.Info("Шрифт «" + taken.TextStyle + "», высота " + SettingsView.FormatValue(taken.TextHeight) +
                                   " м взяты с образца. Нажмите «Применить ко всем точкам» или «Сохранить для новых».");
            });
        }

        private void CreateByHand()
        {
            if (surface != null) CreateByHand(panel, surface.Info.Id);
        }

        /// <summary>Also from the context menu of the tree (for any surface).</summary>
        internal void CreateByHand(KzmPanel owner, string id)
        {
            var settings = owner.Snapshot.Settings;
            PanelActions.RunInteractive("точки вручную", document => EditServices.CreatePointsByHand(document, id, settings), owner.RefreshAll);
        }

        private void PickBoundary()
        {
            if (surface == null) return;
            string handle = null;
            PanelActions.Pick("граница поверхности", document =>
            {
                var options = new PromptEntityOptions("\nУкажите замкнутую полилинию — внешнюю границу поверхности: ");
                options.SetRejectMessage("\nНужна замкнутая полилиния.");
                options.AddAllowedClass(typeof(Curve), false);
                var pick = document.Editor.GetEntity(options);
                if (pick.Status == PromptStatus.OK) handle = pick.ObjectId.Handle.ToString();
            }, () => { if (handle != null) SetBoundary(handle); });
        }

        private void SetBoundary(string handle)
        {
            if (surface == null) return;
            string id = surface.Info.Id;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("граница поверхности", document =>
            {
                EditServices.SetBoundary(document.Database, id, handle, settings);
                PanelMessages.Info(handle == null ? "Граница поверхности снята." : "Граница поверхности задана.");
            }, panel.RefreshAll);
        }

        private void ShowTriangles(bool show)
        {
            if (surface != null) ToggleTriangles(panel, surface.Info.Id, show);
        }

        internal void ToggleTriangles(KzmPanel owner, string id, bool show)
        {
            var settings = owner.Snapshot.Settings;
            PanelActions.Run(show ? "треугольники" : "скрыть треугольники", document =>
            {
                var model = EditServices.ShowTriangles(document.Database, id, show, settings);
                PanelMessages.Info(model == null ? "Треугольники скрыты." :
                    "Треугольников: " + model.Tin.TriangleCount + (model.Tin.ConstrainedEdgeCount > 0 ? ", рёбер структурных линий: " + model.Tin.ConstrainedEdgeCount : string.Empty) +
                    (model.Problems.Count > 0 ? "; " + string.Join("; ", model.Problems) : string.Empty) + ".");
            }, owner.RefreshAll);
        }

        private sealed class LineItem
        {
            internal LineItem(StructureLine line) { Line = line; }
            internal StructureLine Line { get; }
            public override string ToString() =>
                (Line.Kind == StructureLineKind.Flip ? "↔ " : "— ") + Line.Name + " (" + Line.PointHandles.Count + " т." + (Line.Closed ? ", замкнутая" : string.Empty) + ")";
        }

        private void ShowLine()
        {
            if (!(lines.SelectedItem is LineItem item) || panel.Document == null) return;
            var ids = SurfaceTools.PointsOf(panel.Document.Database, item.Line);
            if (ids.Count == 0) { PanelMessages.Error("точек линии нет в чертеже."); return; }
            PanelActions.Show(panel.Document, ids, Commands.KzmCommands.ZoomSize(surface?.Info.Appearance.TextHeight));
        }

        private void DeleteLine()
        {
            if (surface == null || !(lines.SelectedItem is LineItem item)) return;
            var answer = MessageBox.Show(this,
                "Удалить " + (item.Line.Kind == StructureLineKind.Flip ? "заданную диагональ" : "структурную линию") + " «" + item.Line.Name + "»?\n\n" +
                "«Да» — удалить линию и созданные ею точки (точки, бывшие в поверхности раньше, останутся).\n" +
                "«Нет» — удалить только линию, все точки оставить.\n\nОтменить можно командой U.",
                "КЗМ", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return;
            bool erasePoints = answer == DialogResult.Yes;
            string id = surface.Info.Id, lineId = item.Line.Id, name = item.Line.Name;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("удаление структурной линии", document =>
            {
                int erased = SurfaceTools.DeleteLine(document.Database, id, lineId, erasePoints, settings);
                PanelMessages.Info("Линия «" + name + "» удалена" + (erased > 0 ? ", точек удалено: " + erased : string.Empty) + ".");
            }, panel.RefreshAll);
        }

        private void FlipDiagonals()
        {
            if (surface == null) return;
            string id = surface.Info.Id;
            var settings = panel.Snapshot.Settings;
            PanelActions.RunInteractive("замена диагонали", document => SurfaceTools.FlipDiagonals(document, id, settings), panel.RefreshAll);
        }

        private void CheckTin()
        {
            if (surface == null || panel.Document == null) return;
            try
            {
                using (panel.Document.LockDocument(Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Read, null, null, false))
                using (var transaction = panel.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var model = SurfaceModels.Build(panel.Document.Database, transaction, surface.Info, panel.Snapshot.Settings);
                    transaction.Commit();
                    tinNote.Text = "Точек в триангуляции: " + model.Used.Count + ", треугольников: " + model.Tin.TriangleCount +
                                   (model.Tin.ConstrainedEdgeCount > 0 ? ", рёбер структурных линий: " + model.Tin.ConstrainedEdgeCount : string.Empty) +
                                   (model.Problems.Count > 0 ? ".\n" + string.Join(".\n", model.Problems) : string.Empty) + ".";
                }
            }
            catch (KzmException exception)
            {
                tinNote.Text = exception.Message;
            }
        }

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            string typedLayer = layer.Text;
            layer.Items.Clear();
            if (panel.Document != null)
                foreach (var item in panel.Snapshot.Layers.Value) layer.Items.Add(item);
            layer.Text = typedLayer;
            if (surfaceId == null || isNew)
            {
                // A refresh of the panel keeps what the user is typing into the new-surface form.
                if (isNew || !creating)
                {
                    name.Text = string.Empty;
                    (newKind == SurfaceKind.Project ? project : fact).Checked = true;
                    layer.Text = SurfaceInfo.DefaultLayer(newKind);
                }
                isNew = false;
                creating = true;
                surface = null;
                details.Visible = false;
                title.Text = "Новая поверхность";
                info.Text = "Сначала задаётся слой: в него попадут точки этой поверхности. " +
                            "Выберите существующий слой или введите новое имя.";
                save.Text = "Создать";
                save.Visible = true;
                movePoints.Visible = makeCurrent.Visible = import.Visible = delete.Visible = false;
                return;
            }
            creating = false;
            surface = panel.Snapshot.Find(surfaceId);
            if (surface == null)
            {
                title.Text = "Поверхность не найдена";
                details.Visible = false;
                info.Text = "Её нет в текущем чертеже (удалена или открыт другой чертёж).";
                save.Visible = movePoints.Visible = makeCurrent.Visible = import.Visible = delete.Visible = false;
                return;
            }
            title.Text = "Поверхность";
            name.Text = surface.Info.Name;
            (surface.Info.Kind == SurfaceKind.Project ? project : fact).Checked = true;
            layer.Text = surface.Info.Layer;
            save.Text = "Сохранить";
            save.Visible = true;
            movePoints.Visible = false;
            makeCurrent.Visible = !surface.IsCurrent;
            import.Visible = delete.Visible = true;
            info.Text = "Точек: " + surface.Points.ToString(CultureInfo.InvariantCulture) +
                        (surface.BrokenPoints > 0 ? "; с нечитаемой отметкой: " + surface.BrokenPoints : string.Empty) +
                        (surface.PendingPairs > 0 ? "; пар близких точек: " + surface.PendingPairs : string.Empty) +
                        (surface.IsCurrent ? "\nТекущая поверхность." : string.Empty) +
                        (string.IsNullOrEmpty(surface.Info.Created) ? string.Empty : "\nСоздана: " + surface.Info.Created);
            BindDetails();
        }

        private bool creating;

        private void SwapDefaultLayer()
        {
            var kind = project.Checked ? SurfaceKind.Project : SurfaceKind.Fact;
            var other = kind == SurfaceKind.Project ? SurfaceKind.Fact : SurfaceKind.Project;
            if (layer.Text.Trim().Length == 0 || string.Equals(layer.Text.Trim(), SurfaceInfo.DefaultLayer(other), StringComparison.OrdinalIgnoreCase))
                layer.Text = SurfaceInfo.DefaultLayer(kind);
        }

        private void Save()
        {
            string newName, newLayer;
            try
            {
                newName = SurfaceNames.Validate(name.Text);
                newLayer = layer.Text.Trim();
                if (newLayer.Length == 0) throw new KzmException("Укажите слой точек.");
            }
            catch (KzmException exception)
            {
                PanelMessages.Error(exception.Message);
                return;
            }
            string ownId = creating ? null : surface?.Info.Id;
            if (panel.Snapshot.Surfaces.Any(other => other.Info.Id != ownId &&
                    string.Equals(other.Info.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
            {
                PanelMessages.Error("поверхность «" + newName + "» уже есть.");
                return;
            }
            var kind = project.Checked ? SurfaceKind.Project : SurfaceKind.Fact;
            if (creating)
            {
                if (panel.Snapshot.Project == null)
                {
                    PanelMessages.Error("сначала создайте или откройте проект.");
                    return;
                }
                var created = new SurfaceInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = newName,
                    Kind = kind,
                    Layer = newLayer,
                    Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    ProjectId = panel.Snapshot.Project.Id == ProjectInfo.LegacyId ? null : panel.Snapshot.Project.Id,
                };
                PanelActions.Run("новая поверхность", document =>
                {
                    EditServices.CreateSurface(document.Database, created, true);
                    PanelMessages.Info("Создана поверхность " + created.Title + "; она текущая.");
                }, () => panel.AfterSurfaceChange(created.Id));
                return;
            }
            var updated = new SurfaceInfo
            {
                Id = surface.Info.Id,
                Name = newName,
                Kind = kind,
                Layer = newLayer,
                Created = surface.Info.Created,
                Revision = surface.Info.Revision,
            };
            bool move = movePoints.Visible && movePoints.Checked;
            var settings = panel.Snapshot.Settings;
            PanelActions.Run("свойства поверхности", document =>
            {
                int moved = EditServices.UpdateSurface(document.Database, updated, move, settings);
                PanelMessages.Info("Сохранено: " + updated.Title + (moved > 0 ? "; точек перенесено на слой: " + moved : string.Empty) + ".");
            }, () => panel.AfterSurfaceChange(updated.Id));
        }

        private void Delete()
        {
            if (surface != null) Delete(panel, surface.Info.Id);
        }

        /// <summary>Also from the tree (context menu, Delete key).</summary>
        internal void Delete(KzmPanel owner, string id)
        {
            var target = owner.Snapshot.Find(id);
            if (target == null) return;
            var used = owner.Snapshot.Cartograms.Where(c => c.Info.FactSurfaceId == id || c.Info.ProjectSurfaceId == id).Select(c => "«" + c.Info.Name + "»").ToList();
            var answer = MessageBox.Show(owner,
                "Удалить поверхность «" + target.Info.Name + "» и все её точки (" + target.Points + ")?" +
                (target.Lines.Count > 0 ? "\nСтруктурные линии поверхности (" + target.Lines.Count + ") тоже будут удалены." : string.Empty) +
                (used.Count > 0 ? "\n\nОна используется в картограммах " + string.Join(", ", used) + " — их нельзя будет пересчитать." : string.Empty) +
                "\n\nОтменить можно командой U (ОТМЕНИТЬ).",
                "КЗМ", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            string title = target.Info.Name;
            var settings = owner.Snapshot.Settings;
            PanelActions.Run("удаление поверхности", document =>
            {
                int erased = EditServices.DeleteSurface(document.Database, id, settings);
                PanelMessages.Info("Поверхность «" + title + "» удалена, точек удалено: " + erased + ".");
            }, () => owner.AfterSurfaceChange(null));
        }
    }
}
