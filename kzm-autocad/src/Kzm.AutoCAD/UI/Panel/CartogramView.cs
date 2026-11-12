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
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Картограмма: граница работ, поверхности для расчёта, сетка квадратов, оформление,
    /// построение/пересчёт, ведомость объёмов. Всё задаётся в панели; в чертёж — только указать объекты.
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
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        private readonly Button build = new Button { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
        private KzmPanel panel;
        private CartogramSummary cartogram;
        private bool creating;
        private bool newMode;
        private string boundaryHandle;
        private string lastBound;

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
            FlowLayoutPanel Flow(params Control[] controls)
            {
                var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(340, 0) };
                flow.Controls.AddRange(controls);
                return flow;
            }

            Span(title);
            Row("Имя", name);

            Span(Section("Поверхности для расчёта"));
            Row("Фактическая (Hф)", fact);
            Row("Проектная (Hпр)", project);
            Span(new Label
            {
                AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText,
                Text = "Отметки в узлах сетки и в точках границы, где съёмки нет, интерполируются по треугольникам поверхностей; h = Hпр − Hф.",
            });

            Span(Section("Граница работ"));
            Span(boundaryNote);
            var pickBoundary = new Button { Text = "Указать границу…", AutoSize = true };
            pickBoundary.Click += (s, e) => PickBoundary();
            Span(pickBoundary);

            Span(Section("Сетка квадратов"));
            Row("Шаг, м", step);
            Row("Базовая точка", Flow(originAuto, originPoint));
            var pickOrigin = new Button { Text = "Указать…", AutoSize = true };
            pickOrigin.Click += (s, e) => PickOrigin();
            Row("X / Y", Flow(originX, originY, pickOrigin));
            var pickAngle = new Button { Text = "По двум точкам…", AutoSize = true };
            pickAngle.Click += (s, e) => PickAngle();
            Row("Угол поворота, °", Flow(angle, pickAngle));
            foreach (var box in new[] { columns, rows }) { box.Items.Add("1, 2, 3 …"); box.Items.Add("А, Б, В …"); }
            Row("Столбцы", columns);
            Row("Ряды (сверху вниз)", rows);

            Span(Section("Оформление (СТБ 2073-2010)"));
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            Row("Масштаб", scale);
            Row("Текст отметок в узлах, мм", nodeText);
            Row("Текст объёмов, мм", volumeText);
            Row("Текст итогов, мм", totalsText);
            Row("Объёмы: знаков после запятой", volumeDecimals);
            Span(new Label
            {
                AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText,
                Text = "В узле: рабочая отметка — слева вверху, проектная — справа вверху, фактическая — справа внизу (пример Б.4). " +
                       "Линия нулевых работ — штриховая тонкая (п. 3.12), выемки — штриховка 45° (п. 7.1). " +
                       "Под сеткой — таблица по рис. 8, справа — итоги по рядам. Высоты текста — по ряду ГОСТ 2.304, их можно менять.",
            });

            var save = new Button { Text = "Сохранить", AutoSize = true };
            save.Click += (s, e) => Save(false);
            build.Click += (s, e) => Save(true);
            var statement = new Button { Text = "Ведомость объёмов…", AutoSize = true };
            statement.Click += (s, e) => InsertStatement();
            var erase = new Button { Text = "Стереть построение", AutoSize = true };
            erase.Click += (s, e) => EraseDrawing();
            var delete = new Button { Text = "Удалить картограмму…", AutoSize = true };
            delete.Click += (s, e) => Delete();
            Span(Flow(build, save));
            Span(Flow(statement, erase, delete));
            Span(status);

            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            root.Controls.Add(table);
            Controls.Add(root);
            originAuto.CheckedChanged += (s, e) => originX.Enabled = originY.Enabled = originPoint.Checked;
        }

        internal void PrepareNew() => creating = true;

        public void Bind(KzmPanel owner, string cartogramId)
        {
            panel = owner;
            fact.Items.Clear();
            project.Items.Clear();
            foreach (var s in panel.Snapshot.Surfaces)
            {
                (s.Info.Kind == SurfaceKind.Project ? project : fact).Items.Add(new SurfaceItem(s.Info));
            }
            // Either kind may be used on either side if the user wants it (e.g. two project variants).
            foreach (var s in panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Project)) fact.Items.Add(new SurfaceItem(s.Info, true));
            foreach (var s in panel.Snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Fact)) project.Items.Add(new SurfaceItem(s.Info, true));

            if (cartogramId == null)
            {
                // A refresh of the panel keeps what the user is typing into the new-cartogram form.
                if (newMode && !creating) return;
                creating = false;
                newMode = true;
                cartogram = null;
                lastBound = null;
                boundaryHandle = null;
                Display(new CartogramInfo
                {
                    Name = "Картограмма " + (panel.Snapshot.Cartograms.Count + 1),
                    Scale = panel.Snapshot.Settings.Scale,
                    FactSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Fact)?.Info.Id,
                    ProjectSurfaceId = panel.Snapshot.Surfaces.FirstOrDefault(s => s.Info.Kind == SurfaceKind.Project)?.Info.Id,
                }, true);
                return;
            }
            newMode = false;
            cartogram = panel.Snapshot.FindCartogram(cartogramId);
            if (cartogram == null)
            {
                title.Text = "Картограмма не найдена";
                status.Text = string.Empty;
                return;
            }
            if (lastBound != cartogramId) boundaryHandle = cartogram.Info.BoundaryHandle;
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
            build.Text = isNew || !info.Built ? "Построить" : "Пересчитать";
            if (isNew) status.Text = "Заполните поля и нажмите «Построить».";
            else if (!info.Built) status.Text = "Ещё не построена.";
            else
                status.Text = "Построена " + info.BuiltAt + ": насыпь " + CartogramService.Volume(info.LastFill, info.VolumeDecimals, true) +
                              " м³, выемка " + CartogramService.Volume(info.LastCut, info.VolumeDecimals, false) + " м³." +
                              (cartogram != null && cartogram.Outdated ? "\nУСТАРЕЛА: точки, границы или параметры изменились — нажмите «Пересчитать»." : string.Empty);
            status.ForeColor = cartogram != null && cartogram.Outdated ? Color.Firebrick : SystemColors.ControlText;
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
            if (info.FactSurfaceId != null && info.FactSurfaceId == info.ProjectSurfaceId) errors.Add("фактическая и проектная поверхности совпадают");
            errors.AddRange(info.Validate());
            return info;
        }

        private void Save(bool andBuild)
        {
            if (panel?.Document == null) return;
            var errors = new List<string>();
            var info = Read(errors);
            if (errors.Count > 0)
            {
                status.Text = "Исправьте: " + string.Join("; ", errors.Distinct()) + ".";
                status.ForeColor = Color.Firebrick;
                return;
            }
            var settings = panel.Snapshot.Settings;
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
                var computation = CartogramService.Build(document, info.Id, settings);
                var r = computation.Result;
                string text = "Картограмма «" + info.Name + "»: квадратов " + r.Squares.Count + ", насыпь " +
                              CartogramService.Volume(r.TotalFill, info.VolumeDecimals, true) + " м³, выемка " +
                              CartogramService.Volume(r.TotalCut, info.VolumeDecimals, false) + " м³; площадь " +
                              ElevationFormat.Format(r.ComputedArea, 1) + " м² из " + ElevationFormat.Format(r.BoundaryArea, 1) + " м². " +
                              (r.Balanced ? "Проверка: Σ по столбцам = Σ по рядам = итог — сходится." : "ВНИМАНИЕ: суммы по столбцам и рядам не сходятся.");
                document.Editor.WriteMessage("\nКЗМ: " + text);
                foreach (var problem in computation.Problems) document.Editor.WriteMessage("\nКЗМ: " + problem);
                PanelMessages.Info(text + (computation.Problems.Count > 0 ? " Замечаний: " + computation.Problems.Count + " (см. командную строку)." : string.Empty));
            }, () => panel.AfterCartogramChange(info.Id));
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
            Autodesk.AutoCAD.Geometry.Point3d? picked = null;
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

        private void InsertStatement()
        {
            if (cartogram == null) { status.Text = "Сначала сохраните и постройте картограмму."; return; }
            string id = cartogram.Info.Id;
            PanelActions.Run("ведомость объёмов", document =>
            {
                CartogramService.InsertStatement(document, id);
                PanelMessages.Info("Ведомость объёмов земляных масс вставлена (форма рис. 9 СТБ 2073-2010). Строки, кроме «Грунт планировки территории», заполняете вы.");
            });
        }

        private void EraseDrawing()
        {
            if (cartogram == null) return;
            string id = cartogram.Info.Id;
            PanelActions.Run("стереть картограмму", document =>
            {
                CartogramService.EraseDrawing(document.Database, id);
                PanelMessages.Info("Построение картограммы стёрто; параметры сохранены.");
            }, () => panel.AfterCartogramChange(id));
        }

        private void Delete()
        {
            if (cartogram == null) return;
            if (MessageBox.Show(this, "Удалить картограмму «" + cartogram.Info.Name + "» вместе с построением?\nОтменить можно командой U.", "КЗМ",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string id = cartogram.Info.Id;
            PanelActions.Run("удаление картограммы", document =>
            {
                CartogramService.Delete(document.Database, id, true);
                PanelMessages.Info("Картограмма удалена.");
            }, () => panel.AfterCartogramChange(null));
        }
    }
}
