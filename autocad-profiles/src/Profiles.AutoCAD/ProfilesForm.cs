using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>Input-only UI. Drawing and AutoCAD transactions belong to the command adapter.</summary>
    public sealed class ProfilesForm : Form
    {
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage longPage = new TabPage("Продольный профиль");
        private readonly TabPage crossPage = new TabPage("Поперечные профили");
        private readonly TabPage previewPage = new TabPage("Предпросмотр");
        private readonly TabPage formatPage = new TabPage("Оформление");
        private readonly DataGridView longGrid = CreateGrid();
        private readonly DataGridView crossGrid = CreateGrid();
        private readonly ListBox sectionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox title = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox horizontal = Input();
        private readonly TextBox vertical = Input();
        private readonly TextBox datum = Input();
        private readonly TextBox metres = Input();
        private readonly TextBox textHeight = Input();
        private readonly TextBox textWidth = Input();
        private readonly CheckBox emptyRows = new CheckBox { Text = "Сохранять пустые строки таблицы", AutoSize = true };
        private readonly TextBox sectionName = Input();
        private readonly TextBox sectionStation = Input();
        private readonly TextBox sectionWater = Input();
        private readonly TextBox projectBottom = Input();
        private readonly TextBox bottomWidth = Input();
        private readonly TextBox leftSlope = Input();
        private readonly TextBox rightSlope = Input();
        private readonly TextBox centerOffset = Input();
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label modeCaption = new Label { AutoSize = true, ForeColor = Color.FromArgb(45, 70, 90), Margin = new Padding(8, 9, 6, 6) };
        private readonly UI.ProfilePreview preview = new UI.ProfilePreview { Dock = DockStyle.Fill };
        private readonly TextBox previewNotes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 249, 251) };
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000 };
        private readonly List<CrossSection> sections = new List<CrossSection>();
        private ProfileKind activeKind;
        private int selectedSection = -1;
        private bool loading;
        private bool dirty;
        private bool accepted;
        private string currentFile;

        public ProfileProject Project { get; private set; }
        public DrawingPlan Plan { get; private set; }

        public ProfilesForm(ProfileKind kind) : this(kind, null) { }

        public ProfilesForm(ProfileKind kind, ProfileProject initialProject)
        {
            Text = "Профиля — данные и построение";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(980, 690);
            Size = new Size(1240, 850);
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            KeyPreview = true;
            BuildWindow();
            ConfigureLongitudinalGrid();
            ConfigureCrossGrid();
            WireChanges(this);
            sectionList.SelectedIndexChanged += SectionSelectionChanged;
            tabs.Selected += TabSelected;
            FormClosing += ClosingForm;
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveProject(); }
                else if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; OpenProject(); }
            };
            LoadProject(initialProject ?? new ProfileProject
            {
                Kind = kind,
                Settings = new ProfileSettings
                {
                    Title = kind == ProfileKind.Longitudinal ? "ПРОДОЛЬНЫЙ ПРОФИЛЬ" : "ПОПЕРЕЧНЫЕ ПРОФИЛИ",
                    HorizontalScale = kind == ProfileKind.Longitudinal ? 1000 : 100,
                    VerticalScale = 100,
                    MetresPerDrawingUnit = 1,
                    TextHeightMm = 2.5,
                    TextWidthFactor = 0.85
                },
                Longitudinal = new List<LongitudinalPoint>(),
                Sections = new List<CrossSection> { NewSection(1) }
            });
            if (initialProject != null)
            {
                loading = true;
                activeKind = kind;
                tabs.SelectedTab = kind == ProfileKind.Longitudinal ? longPage : crossPage;
                if (title.Text == "ПРОДОЛЬНЫЙ ПРОФИЛЬ" || title.Text == "ПОПЕРЕЧНЫЕ ПРОФИЛИ")
                    title.Text = kind == ProfileKind.Longitudinal ? "ПРОДОЛЬНЫЙ ПРОФИЛЬ" : "ПОПЕРЕЧНЫЕ ПРОФИЛИ";
                UpdateModeCaption();
                loading = false;
                SetStatus("Восстановлены последние подтверждённые данные этого чертежа. Для хранения между сеансами используйте «Сохранить».");
            }
        }

        private void BuildWindow()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 6) };
            toolbar.Controls.Add(Button("Открыть…", OpenProject));
            toolbar.Controls.Add(Button("Сохранить…", SaveProject));
            toolbar.Controls.Add(Button("Загрузить пример", LoadExample));
            toolbar.Controls.Add(modeCaption);
            root.Controls.Add(toolbar, 0, 0);

            var common = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 8, RowCount = 2, Padding = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 8; i++) common.ColumnStyles.Add(new ColumnStyle(i == 1 ? SizeType.Percent : SizeType.AutoSize, i == 1 ? 100 : 0));
            common.Controls.Add(Caption("Название"), 0, 0);
            common.Controls.Add(title, 1, 0);
            common.SetColumnSpan(title, 7);
            common.Controls.Add(Caption("Масштаб по горизонтали 1:"), 0, 1);
            common.Controls.Add(horizontal, 1, 1);
            horizontal.Dock = DockStyle.Left;
            common.Controls.Add(Caption("по вертикали 1:"), 2, 1);
            common.Controls.Add(vertical, 3, 1);
            common.Controls.Add(Caption("Ноль профиля, м"), 4, 1);
            common.Controls.Add(datum, 5, 1);
            common.Controls.Add(new Label { Text = "пусто = автоматически", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 7, 0, 0) }, 6, 1);
            root.Controls.Add(common, 0, 1);
            tips.SetToolTip(datum, "Оставьте пустым: ноль = floor(минимальная отметка) − 1 м. Введённый ноль должен быть ниже всех используемых отметок.");
            tips.SetToolTip(horizontal, "Знаменатель масштаба: например, 1000 для 1:1000. Геометрия создаётся по правилам исходных LISP-шаблонов.");

            tabs.TabPages.AddRange(new[] { longPage, crossPage, previewPage, formatPage });
            root.Controls.Add(tabs, 0, 2);
            BuildLongPage();
            BuildCrossPage();
            BuildPreviewPage();
            BuildFormatPage();
            root.Controls.Add(status, 0, 3);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = Button("Отмена", () => Close());
            close.DialogResult = DialogResult.None;
            footer.Controls.Add(close);
            var draw = Button("Построить в AutoCAD", Draw);
            draw.Font = new Font(Font, FontStyle.Bold);
            draw.BackColor = Color.FromArgb(222, 236, 247);
            footer.Controls.Add(draw);
            footer.Controls.Add(Button("Проверить и показать", () => RefreshPreview(true)));
            root.Controls.Add(footer, 0, 4);
            CancelButton = close;
        }

        private void BuildLongPage()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(Note("Расстояния и отметки — в метрах. ПК: 125,5 или 1+25,50. Берега — по направлению течения.\nПустая ячейка означает отсутствие данных; линия разрывается. Достаточно заполнить нужные столбцы."), 0, 0);
            layout.Controls.Add(longGrid, 0, 1);
            layout.Controls.Add(GridToolbar(longGrid), 0, 2);
            longPage.Controls.Add(layout);
        }

        private void BuildCrossPage()
        {
            var split = new SplitContainer { Size = new Size(1100, 450), Dock = DockStyle.Fill, SplitterDistance = 190, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 150, Panel2MinSize = 550 };
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(Caption("Список поперечников"), 0, 0);
            left.Controls.Add(sectionList, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.Add(Button("+", AddSection));
            buttons.Controls.Add(Button("Копия", CopySection));
            buttons.Controls.Add(Button("−", RemoveSection));
            left.Controls.Add(buttons, 0, 2);
            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(8) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = Fields(
                "Название", sectionName,
                "ПК / станция, м", sectionStation,
                "Урез воды, м", sectionWater);
            right.Controls.Add(header, 0, 0);
            var designBox = new GroupBox { Text = "Проект канала — трапеция (необязательно)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 20, 8, 8) };
            var design = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 3, ColumnCount = 1 };
            design.Controls.Add(Fields("Дно, м", projectBottom, "Ширина дна, м", bottomWidth, "Центр, м", centerOffset), 0, 0);
            design.Controls.Add(Fields("Откос слева 1:", leftSlope, "Откос справа 1:", rightSlope), 0, 1);
            design.Controls.Add(Note("Заполните дно, ширину и оба откоса либо оставьте все четыре поля пустыми.\nДля произвольного проекта пруда используйте столбец «Проект» вместо трапеции."), 0, 2);
            designBox.Controls.Add(design);
            right.Controls.Add(designBox, 0, 1);
            right.Controls.Add(Note("Смещения идут слева направо, в метрах; допускаются отрицательные.\nНужны минимум две точки. Пустой урез воды или проект не рисуется."), 0, 2);
            right.Controls.Add(crossGrid, 0, 3);
            right.Controls.Add(GridToolbar(crossGrid), 0, 4);
            split.Panel2.Controls.Add(right);
            crossPage.Controls.Add(split);
            tips.SetToolTip(leftSlope, "Заложение — горизонталь / вертикаль: для откоса 1:1,5 введите 1,5.");
            tips.SetToolTip(rightSlope, "Правый откос может отличаться от левого. Требуется положительное число.");
            tips.SetToolTip(centerOffset, "Смещение центра проектного дна относительно системы смещений поперечника. По умолчанию 0 м.");
            tips.SetToolTip(sectionWater, "Необязательная отметка горизонтального уровня воды. Линия ограничивается затопленными участками измеренной поверхности.");
        }

        private void BuildPreviewPage()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6) };
            bar.Controls.Add(Button("Обновить", () => RefreshPreview(false)));
            bar.Controls.Add(Button("Вписать", preview.Fit));
            bar.Controls.Add(new Label { Text = "Колесо — масштаб; перетаскивание — перемещение. Шрифт в AutoCAD определяется текущим стилем.", AutoSize = true, Margin = new Padding(8, 9, 0, 0) });
            layout.Controls.Add(bar, 0, 0);
            layout.Controls.Add(preview, 0, 1);
            layout.Controls.Add(previewNotes, 0, 2);
            previewPage.Controls.Add(layout);
        }

        private void BuildFormatPage()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(20) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(Caption("Метров в одной единице чертежа"), 0, 0);
            layout.Controls.Add(metres, 1, 0);
            layout.Controls.Add(Caption("Высота текста на бумаге, мм"), 0, 1);
            layout.Controls.Add(textHeight, 1, 1);
            layout.Controls.Add(Caption("Коэффициент ширины текста"), 0, 2);
            layout.Controls.Add(textWidth, 1, 2);
            layout.Controls.Add(emptyRows, 1, 3);
            var explanation = Note(
                "Все исходные расстояния и отметки вводятся в метрах.\n" +
                "Если одна единица чертежа равна метру — введите 1; миллиметру — 0,001.\n\n" +
                "Горизонтальные координаты = расстояние / метров в единице.\n" +
                "Вертикаль = (отметка − ноль) × горизонтальный масштаб / вертикальный масштаб / метров в единице.\n" +
                "Размер текста и строк таблицы согласован с масштабом, как в предоставленных LISP-шаблонах.\n\n" +
                "Факт — чёрный/белый; проект — красный; вода — синий; левый берег — зелёный;\n" +
                "правый берег — коричневый штриховой. Результат — редактируемые объекты AutoCAD.\n" +
                "После закрытия окна кнопкой «Построить» укажите точку вставки в текущей системе координат.\n\n" +
                "Сохраняйте исходные данные кнопкой «Сохранить»: JSON можно открыть и изменить позднее.\n" +
                "Программа не рассчитывает объёмы земляных работ и не заменяет проверку проектировщиком.");
            explanation.MaximumSize = new Size(950, 0);
            layout.Controls.Add(explanation, 0, 4);
            layout.SetColumnSpan(explanation, 2);
            formatPage.Controls.Add(layout);
            formatPage.AutoScroll = true;
        }

        private void ConfigureLongitudinalGrid()
        {
            AddColumn(longGrid, "Station", "ПК / станция, м", "Расстояние от начала, м, либо запись ПК+метры: 1+25,50.", 115);
            AddColumn(longGrid, "Ground", "Земля, м", "Поверхность земли по оси; необязательно.", 95);
            AddColumn(longGrid, "LeftBank", "Левый берег, м", "По направлению течения; необязательно.", 110);
            AddColumn(longGrid, "RightBank", "Правый берег, м", "По направлению течения; необязательно.", 110);
            AddColumn(longGrid, "ExistingBottom", "Дно факт., м", "Существующая отметка дна; необязательно.", 100);
            AddColumn(longGrid, "Water", "Урез воды, м", "Отметка воды; необязательно.", 100);
            AddColumn(longGrid, "ProjectBottom", "Дно проект, м", "Проектная отметка дна / подчистки; необязательно.", 110);
            AddColumn(longGrid, "BottomWidth", "Ширина дна, м", "Проектная ширина в таблице; не задаёт отметку проекта.", 105);
            AddColumn(longGrid, "LeftSlope", "Откос слева 1:", "Горизонталь / вертикаль; например, 1,5.", 110);
            AddColumn(longGrid, "RightSlope", "Откос справа 1:", "Горизонталь / вертикаль; например, 1,5.", 110);
            longGrid.Columns[0].Frozen = true;
            longGrid.Columns[6].DefaultCellStyle.ForeColor = Color.Firebrick;
            longGrid.Columns[5].DefaultCellStyle.ForeColor = Color.RoyalBlue;
            longGrid.Columns[2].DefaultCellStyle.ForeColor = Color.DarkGreen;
            longGrid.Columns[3].DefaultCellStyle.ForeColor = Color.SaddleBrown;
        }

        private void ConfigureCrossGrid()
        {
            AddColumn(crossGrid, "Offset", "Смещение, м", "Слева направо; отрицательные значения допустимы.", 150);
            AddColumn(crossGrid, "Existing", "Поверхность / факт, м", "Измеренная поверхность, дно и берега.", 180);
            AddColumn(crossGrid, "Design", "Проект, м", "Произвольная проектная линия; не совмещайте с параметрами трапеции.", 180);
            crossGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            crossGrid.Columns[2].DefaultCellStyle.ForeColor = Color.Firebrick;
        }

        private static DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AllowUserToOrderColumns = false,
                AutoGenerateColumns = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = true,
                RowHeadersWidth = 55,
                ColumnHeadersHeight = 46,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
            };
            grid.DefaultCellStyle.NullValue = "";
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            grid.RowPostPaint += (s, e) =>
            {
                var bounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth - 6, e.RowBounds.Height);
                TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(CultureInfo.InvariantCulture), grid.Font, bounds, Color.DimGray,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            };
            return grid;
        }

        private void AddColumn(DataGridView grid, string name, string header, string tooltip, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, ToolTipText = tooltip,
                Width = width, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable,
                ValueType = typeof(string)
            });
        }

        private Control GridToolbar(DataGridView grid)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
            bar.Controls.Add(Button("Вставить из Excel", () => Paste(grid)));
            bar.Controls.Add(Button("Удалить строки", () => DeleteRows(grid)));
            bar.Controls.Add(new Label { Text = "Ctrl+V — вставить таблицу без заголовков; Ctrl+C — копировать выделение.", AutoSize = true, Margin = new Padding(8, 9, 0, 0) });
            var menu = new ContextMenuStrip();
            menu.Items.Add("Вставить из Excel", null, (s, e) => Paste(grid));
            menu.Items.Add("Удалить выбранные строки", null, (s, e) => DeleteRows(grid));
            grid.ContextMenuStrip = menu;
            grid.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.V) { e.SuppressKeyPress = true; Paste(grid); }
            };
            return bar;
        }

        private void Paste(DataGridView grid)
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string raw = Clipboard.GetText(TextDataFormat.UnicodeText).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                if (raw.Length == 0) return;
                string[][] values = raw.Split('\n').Select(line => line.Split('\t')).ToArray();
                int row = grid.CurrentCell == null ? 0 : grid.CurrentCell.RowIndex;
                int column = grid.CurrentCell == null ? 0 : grid.CurrentCell.ColumnIndex;
                if (values.Length + row > 10000) throw new InvalidOperationException("Слишком много строк: максимум 10 000.");
                if (values.Any(line => line.Length + column > grid.ColumnCount))
                    throw new InvalidOperationException("Вставляемая таблица выходит за последний столбец. Выберите первую ячейку и копируйте только нужные столбцы без заголовков.");
                grid.EndEdit();
                while (grid.Rows.Count - 1 < row + values.Length) grid.Rows.Add();
                for (int y = 0; y < values.Length; y++)
                    for (int x = 0; x < values[y].Length; x++)
                        grid.Rows[row + y].Cells[column + x].Value = values[y][x].Trim();
                SetDirty();
                SetStatus("Вставлено строк: " + values.Length + ". Проверьте соответствие столбцов перед построением.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void DeleteRows(DataGridView grid)
        {
            var indexes = grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex)
                .Concat(grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index))
                .Distinct().OrderByDescending(i => i).Where(i => !grid.Rows[i].IsNewRow).ToList();
            if (indexes.Count == 0) return;
            if (MessageBox.Show(this, "Удалить выбранные строки: " + indexes.Count + "?", "Удаление строк", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (int index in indexes) grid.Rows.RemoveAt(index);
            SetDirty();
        }

        private void TabSelected(object sender, TabControlEventArgs e)
        {
            if (loading) return;
            if (e.TabPage == longPage || e.TabPage == crossPage)
            {
                var next = e.TabPage == longPage ? ProfileKind.Longitudinal : ProfileKind.CrossSection;
                if (next != activeKind)
                {
                    if (title.Text == "ПРОДОЛЬНЫЙ ПРОФИЛЬ" || title.Text == "ПОПЕРЕЧНЫЕ ПРОФИЛИ")
                        title.Text = next == ProfileKind.Longitudinal ? "ПРОДОЛЬНЫЙ ПРОФИЛЬ" : "ПОПЕРЕЧНЫЕ ПРОФИЛИ";
                    activeKind = next;
                    SetDirty();
                }
                UpdateModeCaption();
            }
        }

        private void UpdateModeCaption()
        {
            modeCaption.Text = activeKind == ProfileKind.Longitudinal ? "Построение: продольный профиль" : "Построение: все поперечники из списка";
        }

        private ProfileSettings ReadSettings()
        {
            return new ProfileSettings
            {
                Title = title.Text.Trim(),
                HorizontalScale = NumberParser.ParseRequired(horizontal.Text, "Горизонтальный масштаб"),
                VerticalScale = NumberParser.ParseRequired(vertical.Text, "Вертикальный масштаб"),
                Datum = NumberParser.ParseOptional(datum.Text, "Ноль профиля"),
                MetresPerDrawingUnit = NumberParser.ParseRequired(metres.Text, "Метров в единице чертежа"),
                TextHeightMm = NumberParser.ParseRequired(textHeight.Text, "Высота текста"),
                TextWidthFactor = NumberParser.ParseRequired(textWidth.Text, "Коэффициент ширины текста"),
                ShowEmptyRows = emptyRows.Checked
            };
        }

        private List<LongitudinalPoint> ReadLongitudinal()
        {
            longGrid.EndEdit();
            var result = new List<LongitudinalPoint>();
            foreach (DataGridViewRow row in longGrid.Rows)
            {
                if (row.IsNewRow || RowEmpty(row)) continue;
                result.Add(new LongitudinalPoint
                {
                    Station = Required(row, "Station"), Ground = Optional(row, "Ground"),
                    LeftBank = Optional(row, "LeftBank"), RightBank = Optional(row, "RightBank"),
                    ExistingBottom = Optional(row, "ExistingBottom"), Water = Optional(row, "Water"),
                    ProjectBottom = Optional(row, "ProjectBottom"), BottomWidth = Optional(row, "BottomWidth"),
                    LeftSlope = Optional(row, "LeftSlope"), RightSlope = Optional(row, "RightSlope")
                });
            }
            return result;
        }

        private static bool RowEmpty(DataGridViewRow row)
        {
            return row.Cells.Cast<DataGridViewCell>().All(c => string.IsNullOrWhiteSpace(Convert.ToString(c.Value, CultureInfo.InvariantCulture)));
        }

        private static double Required(DataGridViewRow row, string column)
        {
            var cell = row.Cells[column];
            return NumberParser.ParseRequired(Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                "Строка " + (row.Index + 1) + ", «" + cell.OwningColumn.HeaderText + "»");
        }

        private static double? Optional(DataGridViewRow row, string column)
        {
            var cell = row.Cells[column];
            return NumberParser.ParseOptional(Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                "Строка " + (row.Index + 1) + ", «" + cell.OwningColumn.HeaderText + "»");
        }

        private CrossSection ReadCurrentSection()
        {
            crossGrid.EndEdit();
            var result = new CrossSection
            {
                Name = sectionName.Text.Trim(),
                Station = NumberParser.ParseOptional(sectionStation.Text, "ПК поперечника"),
                Water = NumberParser.ParseOptional(sectionWater.Text, "Урез воды поперечника"),
                ProjectBottom = NumberParser.ParseOptional(projectBottom.Text, "Проектное дно поперечника"),
                BottomWidth = NumberParser.ParseOptional(bottomWidth.Text, "Ширина проектного дна"),
                LeftSlope = NumberParser.ParseOptional(leftSlope.Text, "Левый откос"),
                RightSlope = NumberParser.ParseOptional(rightSlope.Text, "Правый откос"),
                CenterOffset = NumberParser.ParseOptional(centerOffset.Text, "Центр проектного дна") ?? 0,
                Points = new List<SectionPoint>()
            };
            foreach (DataGridViewRow row in crossGrid.Rows)
            {
                if (row.IsNewRow || RowEmpty(row)) continue;
                result.Points.Add(new SectionPoint { Offset = Required(row, "Offset"), Existing = Optional(row, "Existing"), Design = Optional(row, "Design") });
            }
            return result;
        }

        private void CommitSection()
        {
            if (selectedSection < 0 || selectedSection >= sections.Count) return;
            sections[selectedSection] = ReadCurrentSection();
            if (selectedSection < sectionList.Items.Count)
            {
                bool old = loading;
                loading = true;
                sectionList.Items[selectedSection] = (selectedSection + 1) + ". " +
                    (string.IsNullOrWhiteSpace(sections[selectedSection].Name) ? "Без названия" : sections[selectedSection].Name);
                loading = old;
            }
        }

        private void SectionSelectionChanged(object sender, EventArgs e)
        {
            if (loading || sectionList.SelectedIndex == selectedSection) return;
            int next = sectionList.SelectedIndex;
            try { CommitSection(); }
            catch (Exception ex)
            {
                loading = true;
                sectionList.SelectedIndex = selectedSection;
                loading = false;
                ShowError(ex.Message);
                return;
            }
            selectedSection = next;
            RefreshSectionList(next);
            DisplaySection();
        }

        private void DisplaySection()
        {
            bool old = loading;
            loading = true;
            var section = selectedSection >= 0 && selectedSection < sections.Count ? sections[selectedSection] : NewSection(1);
            sectionName.Text = section.Name ?? "";
            sectionStation.Text = Format(section.Station);
            sectionWater.Text = Format(section.Water);
            projectBottom.Text = Format(section.ProjectBottom);
            bottomWidth.Text = Format(section.BottomWidth);
            leftSlope.Text = Format(section.LeftSlope);
            rightSlope.Text = Format(section.RightSlope);
            centerOffset.Text = Format(section.CenterOffset);
            crossGrid.Rows.Clear();
            foreach (var point in section.Points ?? new List<SectionPoint>())
                crossGrid.Rows.Add(Format(point.Offset), Format(point.Existing), Format(point.Design));
            loading = old;
        }

        private void RefreshSectionList(int selection)
        {
            bool old = loading;
            loading = true;
            sectionList.Items.Clear();
            for (int i = 0; i < sections.Count; i++)
                sectionList.Items.Add((i + 1) + ". " + (string.IsNullOrWhiteSpace(sections[i].Name) ? "Без названия" : sections[i].Name));
            sectionList.SelectedIndex = selection >= 0 && selection < sections.Count ? selection : -1;
            loading = old;
        }

        private void AddSection()
        {
            try
            {
                CommitSection();
                sections.Add(NewSection(sections.Count + 1));
                selectedSection = sections.Count - 1;
                RefreshSectionList(selectedSection);
                DisplaySection();
                SetDirty();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void CopySection()
        {
            try
            {
                CommitSection();
                if (selectedSection < 0) return;
                var source = sections[selectedSection];
                var copy = CloneSection(source);
                copy.Name = (source.Name ?? "Поперечник") + " — копия";
                sections.Insert(selectedSection + 1, copy);
                selectedSection++;
                RefreshSectionList(selectedSection);
                DisplaySection();
                SetDirty();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void RemoveSection()
        {
            if (selectedSection < 0) return;
            if (MessageBox.Show(this, "Удалить поперечник «" + sectionName.Text + "»?", "Удаление поперечника", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            sections.RemoveAt(selectedSection);
            if (sections.Count == 0) sections.Add(NewSection(1));
            selectedSection = Math.Min(selectedSection, sections.Count - 1);
            RefreshSectionList(selectedSection);
            DisplaySection();
            SetDirty();
        }

        private ProfileProject ReadProject()
        {
            Validate();
            CommitSection();
            var project = new ProfileProject
            {
                SchemaVersion = 1,
                Kind = activeKind,
                Settings = ReadSettings(),
                Longitudinal = ReadLongitudinal(),
                Sections = sections.Select(CloneSection).ToList()
            };
            return project;
        }

        private void LoadProject(ProfileProject project)
        {
            loading = true;
            try
            {
                activeKind = project.Kind;
                var settings = project.Settings ?? new ProfileSettings();
                title.Text = settings.Title ?? "";
                horizontal.Text = Format(settings.HorizontalScale);
                vertical.Text = Format(settings.VerticalScale);
                datum.Text = Format(settings.Datum);
                metres.Text = Format(settings.MetresPerDrawingUnit);
                textHeight.Text = Format(settings.TextHeightMm);
                textWidth.Text = Format(settings.TextWidthFactor);
                emptyRows.Checked = settings.ShowEmptyRows;
                longGrid.Rows.Clear();
                foreach (var point in project.Longitudinal ?? new List<LongitudinalPoint>())
                    longGrid.Rows.Add(Format(point.Station), Format(point.Ground), Format(point.LeftBank), Format(point.RightBank),
                        Format(point.ExistingBottom), Format(point.Water), Format(point.ProjectBottom), Format(point.BottomWidth), Format(point.LeftSlope), Format(point.RightSlope));
                sections.Clear();
                sections.AddRange((project.Sections ?? new List<CrossSection>()).Select(CloneSection));
                if (sections.Count == 0) sections.Add(NewSection(1));
                selectedSection = 0;
                RefreshSectionList(0);
                DisplaySection();
                tabs.SelectedTab = activeKind == ProfileKind.Longitudinal ? longPage : crossPage;
                UpdateModeCaption();
                Project = project;
                Plan = null;
                preview.SetPlan(null);
                previewNotes.Text = "";
                dirty = false;
                SetStatus("Введите данные или загрузите пример. Пустые значения не заменяются нулём.");
            }
            finally { loading = false; }
            UpdateTitle();
        }

        private void RefreshPreview(bool switchTab)
        {
            try
            {
                var project = ReadProject();
                var plan = ProfileEngine.Build(project);
                Project = project;
                Plan = plan;
                preview.SetPlan(plan);
                previewNotes.Text = plan.Warnings.Count == 0 ? "Проверка завершена без замечаний." : string.Join(Environment.NewLine, plan.Warnings);
                if (switchTab) tabs.SelectedTab = previewPage;
                SetStatus("Данные проверены. Ноль: " + Format(plan.Datum) + " м. " +
                    (plan.Warnings.Count == 0 ? "Можно построить в AutoCAD." : "Замечаний: " + plan.Warnings.Count + ". Подробности в предпросмотре."), plan.Warnings.Count > 0);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void Draw()
        {
            try
            {
                var project = ReadProject();
                var plan = ProfileEngine.Build(project);
                preview.SetPlan(plan);
                previewNotes.Text = plan.Warnings.Count == 0 ? "Проверка завершена без замечаний." : string.Join(Environment.NewLine, plan.Warnings);
                if (plan.Warnings.Count > 0 && MessageBox.Show(this,
                    string.Join(Environment.NewLine, plan.Warnings.Take(12)) +
                    (plan.Warnings.Count > 12 ? "\n\nЕщё замечаний: " + (plan.Warnings.Count - 12) + ". Полный список находится на вкладке «Предпросмотр»." : "") + "\n\nПродолжить построение?",
                    "Замечания к профилю", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                Project = project;
                Plan = plan;
                accepted = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void OpenProject()
        {
            if (!ConfirmDiscard()) return;
            using (var dialog = new OpenFileDialog { Filter = "Данные профилей (*.json)|*.json|Все файлы (*.*)|*.*", Title = "Открыть данные профилей", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var project = ProjectFile.Load(dialog.FileName);
                    LoadProject(project);
                    currentFile = dialog.FileName;
                    UpdateTitle();
                    SetStatus("Открыто: " + Path.GetFileName(currentFile));
                }
                catch (Exception ex) { ShowError("Не удалось открыть файл: " + ex.Message); }
            }
        }

        private void SaveProject()
        {
            try
            {
                var project = ReadProject();
                using (var dialog = new SaveFileDialog
                {
                    Filter = "Данные профилей (*.json)|*.json", Title = "Сохранить данные профилей",
                    AddExtension = true, DefaultExt = "json", FileName = currentFile == null ? "Профили.json" : Path.GetFileName(currentFile),
                    InitialDirectory = currentFile == null ? "" : Path.GetDirectoryName(currentFile), OverwritePrompt = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    ProjectFile.Save(dialog.FileName, project);
                    Project = project;
                    currentFile = dialog.FileName;
                    dirty = false;
                    UpdateTitle();
                    SetStatus("Данные сохранены: " + Path.GetFileName(currentFile));
                }
            }
            catch (Exception ex) { ShowError("Не удалось сохранить: " + ex.Message); }
        }

        private void LoadExample()
        {
            if (!ConfirmDiscard()) return;
            var sample = activeKind == ProfileKind.Longitudinal ? SampleProjects.Longitudinal() : SampleProjects.CrossSections();
            LoadProject(sample);
            currentFile = null;
            SetDirty();
            SetStatus("Загружен демонстрационный пример. Замените значения своими перед построением.");
        }

        private bool ConfirmDiscard()
        {
            if (!dirty) return true;
            var result = MessageBox.Show(this, "Текущие данные изменены. Сохранить их перед продолжением?", "Данные профилей", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) return false;
            if (result == DialogResult.Yes) { SaveProject(); return !dirty; }
            return true;
        }

        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            if (accepted) return;
            // A click on the window close button must commit the active editor
            // before deciding whether there are unsaved grid changes.
            longGrid.EndEdit();
            crossGrid.EndEdit();
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            DialogResult = DialogResult.Cancel;
        }

        private void WireChanges(Control control)
        {
            if (control is TextBox box && !box.ReadOnly) box.TextChanged += (s, e) => SetDirty();
            if (control is CheckBox check) check.CheckedChanged += (s, e) => SetDirty();
            if (control is DataGridView grid)
            {
                grid.CellValueChanged += (s, e) => SetDirty();
                grid.RowsRemoved += (s, e) => SetDirty();
                grid.UserAddedRow += (s, e) => SetDirty();
            }
            foreach (Control child in control.Controls) WireChanges(child);
        }

        private void SetDirty()
        {
            if (loading) return;
            dirty = true;
            Plan = null;
            preview.MarkStale();
            previewNotes.Text = "Данные изменены. Нажмите «Обновить» для повторной проверки.";
            UpdateTitle();
            SetStatus("Есть изменения. Обновите предпросмотр или нажмите «Построить в AutoCAD».");
        }

        private void UpdateTitle()
        {
            Text = "Профиля — " + (currentFile == null ? "новые данные" : Path.GetFileName(currentFile)) + (dirty ? " *" : "");
        }

        private void SetStatus(string message, bool warning = false)
        {
            status.Text = message;
            status.ForeColor = warning ? Color.DarkOrange : Color.FromArgb(45, 70, 90);
            tips.SetToolTip(status, message);
        }

        private void ShowError(string message)
        {
            SetStatus(message, true);
            MessageBox.Show(this, message, "Проверьте данные", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string Format(double? value) => value.HasValue ? value.Value.ToString("0.###############", CultureInfo.InvariantCulture) : "";

        private static CrossSection NewSection(int number) => new CrossSection { Name = number + "-" + number, Points = new List<SectionPoint>(), CenterOffset = 0 };

        private static CrossSection CloneSection(CrossSection value)
        {
            return new CrossSection
            {
                Name = value.Name, Station = value.Station, Water = value.Water,
                ProjectBottom = value.ProjectBottom, BottomWidth = value.BottomWidth,
                LeftSlope = value.LeftSlope, RightSlope = value.RightSlope, CenterOffset = value.CenterOffset,
                Points = (value.Points ?? new List<SectionPoint>()).Select(p => new SectionPoint { Offset = p.Offset, Existing = p.Existing, Design = p.Design }).ToList()
            };
        }

        private static TextBox Input() => new TextBox { Width = 100, Margin = new Padding(3, 4, 12, 4) };

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 7, 7) };

        private static Label Note(string text) => new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = new Padding(3, 4, 3, 9) };

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(text.Length < 3 ? 36 : 85, 30), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(3) };
            button.Click += (s, e) => action();
            return button;
        }

        private static Control Fields(params object[] values)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            for (int i = 0; i < values.Length; i += 2)
            {
                var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                group.Controls.Add(Caption((string)values[i]));
                group.Controls.Add((Control)values[i + 1]);
                panel.Controls.Add(group);
            }
            return panel;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // DataGridView's editing textbox otherwise consumes Ctrl+V and puts
            // an entire Excel range into one cell instead of distributing it.
            if (keyData == (Keys.Control | Keys.V))
            {
                if (longGrid.ContainsFocus) { Paste(longGrid); return true; }
                if (crossGrid.ContainsFocus) { Paste(crossGrid); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
