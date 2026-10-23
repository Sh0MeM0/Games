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
    public sealed partial class ProfilesForm : Form
    {
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage longPage = new TabPage("Продольный профиль");
        private readonly TabPage crossPage = new TabPage("Поперечные профили");
        private readonly TabPage previewPage = new TabPage("Предпросмотр");
        private readonly TabPage formatPage = new TabPage("Оформление");
        private readonly DataGridView longGrid = CreateGrid();
        private readonly UI.SectionPointGrid crossGrid = new UI.SectionPointGrid { Dock = DockStyle.Fill };
        private readonly ListBox sectionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly TextBox title = new TextBox { Dock = DockStyle.Fill };
        private readonly Label titleCaption = Caption("Название");
        private readonly TextBox horizontal = Input();
        private readonly TextBox vertical = Input();
        private readonly TextBox datum = Input();
        private readonly TextBox metres = Input();
        private readonly TextBox textHeight = Input();
        private readonly TextBox textWidth = Input();
        private readonly TextBox sectionName = Input();
        private readonly ComboBox sectionStation = new ComboBox { Width = 135, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(2, 3, 5, 3) };
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
        public bool RebuildExisting { get; private set; }

        public ProfilesForm(ProfileKind kind) : this(kind, null) { }

        public ProfilesForm(ProfileKind kind, ProfileProject initialProject) : this(kind, initialProject, null, null) { }

        public ProfilesForm(ProfileKind kind, ProfileProject initialProject, IReadOnlyList<ProfileProject> availableProjects, Action<ProfileProject> saveProjectToDrawing, string selectedSectionId = null)
        {
            this.saveProjectToDrawing = saveProjectToDrawing;
            Text = "Профиля — данные и построение";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1060, 700);
            Size = new Size(1540, 940);
            Font = new Font("Segoe UI", 8.5F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            KeyPreview = true;
            BuildWindow();
            ConfigureLongitudinalGrid();
            InitializeLongitudinalPrecision();
            ConfigureCrossGrid();
            WireChanges(this);
            InitializeInterpolation();
            InitializeRowOrdering();
            InitializeSharedEditing();
            InitializeProjectSupport(availableProjects);
            sectionList.SelectedIndexChanged += SectionSelectionChanged;
            tabs.Selected += TabSelected;
            FormClosing += ClosingForm;
            Shown += (s, e) =>
            {
                var area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height)
                    Bounds = new Rectangle(area.Left + 8, area.Top + 8, Math.Max(MinimumSize.Width, area.Width - 16), Math.Max(MinimumSize.Height, area.Height - 16));
            };
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveDraftExplicit(); }
                else if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; OpenProject(); }
            };
            LoadProject(initialProject ?? new ProfileProject
            {
                Kind = kind,
                Settings = new ProfileSettings
                {
                    Title = "ПРОДОЛЬНЫЙ ПРОФИЛЬ",
                    HorizontalScale = 1000,
                    VerticalScale = 100,
                    CrossHorizontalScale = 100,
                    CrossVerticalScale = 100,
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
                DisplayActiveScales();
                tabs.SelectedTab = kind == ProfileKind.Longitudinal ? longPage : crossPage;
                UpdateModeCaption();
                loading = false;
                SetStatus(saveProjectToDrawing == null
                    ? "Восстановлены последние данные. Для хранения между сеансами экспортируйте JSON."
                    : "Восстановлены данные проекта из DWG. После закрытия окна сохраните сам чертёж AutoCAD.");
            }
            RestoreSectionSelection(selectedSectionId);
        }

        private void BuildWindow()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            root.Controls.Add(BuildProjectBar(), 0, 0);

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0) };
            toolbar.Controls.Add(Button("Импорт JSON…", OpenProject));
            toolbar.Controls.Add(Button("Экспорт JSON…", SaveProject));
            toolbar.Controls.Add(autoSaveJson);
            toolbar.Controls.Add(Button("Загрузить пример", LoadExample));
            toolbar.Controls.Add(modeCaption);
            root.Controls.Add(toolbar, 0, 1);

            var common = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 8, RowCount = 2, Padding = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 8; i++) common.ColumnStyles.Add(new ColumnStyle(i == 1 ? SizeType.Percent : SizeType.AutoSize, i == 1 ? 100 : 0));
            common.Controls.Add(titleCaption, 0, 0);
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
            root.Controls.Add(common, 0, 2);
            tips.SetToolTip(datum, "Оставьте пустым: ноль = floor(минимальная отметка) − 1 м. Введённый ноль должен быть ниже всех используемых отметок.");
            tips.SetToolTip(horizontal, "Знаменатель масштаба: например, 1000 для 1:1000. Геометрия создаётся по правилам исходных LISP-шаблонов.");

            tabs.TabPages.AddRange(new[] { longPage, crossPage, previewPage, formatPage });
            root.Controls.Add(BuildProfilesBar(), 0, 3);
            root.Controls.Add(tabs, 0, 4);
            BuildLongPage();
            BuildCrossPage();
            BuildPreviewPage();
            BuildFormatPage();
            root.Controls.Add(status, 0, 5);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            var close = Button("Закрыть", () => Close());
            close.DialogResult = DialogResult.None;
            footer.Controls.Add(close);
            var draw = buildButton = Button("Построить / переместить", () => Draw(false));
            var rebuild = rebuildButton = Button("Перестроить на месте", () => Draw(true));
            tips.SetToolTip(draw, "Построить выбранный тип профиля текущего проекта в указанной точке. Если он уже построен, прежний результат этого проекта заменяется.");
            tips.SetToolTip(rebuild, "Заменить ранее построенный профиль этого проекта и типа, сохранив точку вставки и ориентацию.");
            draw.Font = new Font(Font, FontStyle.Bold);
            draw.BackColor = Color.FromArgb(222, 236, 247);
            footer.Controls.Add(draw);
            footer.Controls.Add(rebuild);
            footer.Controls.Add(Button("Проверить и показать", () => RefreshPreview(true)));
            footer.Controls.Add(BuildScopeControl());
            root.Controls.Add(footer, 0, 6);
            CancelButton = close;
        }

        private void BuildLongPage()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(Note("ПК 6+50 = 650 м; 6 = 6 м. Курсив — интерполяция. Пикеты упорядочиваются после ввода; берега — по течению."), 0, 0);
            layout.Controls.Add(longGrid, 0, 1);
            layout.Controls.Add(GridToolbar(longGrid), 0, 2);
            longPage.Controls.Add(layout);
        }

        private void BuildCrossPage()
        {
            var layout = crossLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(3) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(4) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(Caption("ПК · 6+50 = 650 м"), 0, 0);
            sectionStation.Dock = DockStyle.Top;
            left.Controls.Add(sectionStation, 0, 1);
            left.Controls.Add(Button("Обновить по ПК", RefreshStationSeed), 0, 2);
            left.Controls.Add(sectionList, 0, 3);
            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            listButtons.Controls.Add(Button("+ до", () => InsertSection(false)));
            listButtons.Controls.Add(Button("+ после", () => InsertSection(true)));
            listButtons.Controls.Add(Button("Копия", CopySection));
            listButtons.Controls.Add(Button("−", RemoveSection));
            foreach (Button button in listButtons.Controls)
                button.MinimumSize = new Size(50, 26);
            left.Controls.Add(listButtons, 0, 4);
            layout.Controls.Add(left, 0, 0);

            var center = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(3) };
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = (FlowLayoutPanel)Fields("Профиль", sectionName, "Вода, м", sectionWater);
            header.Controls.Add(showEarthworks);
            center.Controls.Add(header, 0, 0);
            center.Controls.Add(BuildCrossDesignPanel(), 0, 1);
            center.Controls.Add(Note("Слева направо по течению · интервалы по съёмке · пусто = неизвестно"), 0, 2);
            center.Controls.Add(crossGrid, 0, 3);
            center.Controls.Add(BuildPointToolbar(), 0, 4);
            center.Controls.Add(Note("«Назначить» в шапке: берега / дно · Пустая отметка: ▼ → интерполяция · Красная ячейка: проектное дно · Ctrl+V: Excel"), 0, 5);
            layout.Controls.Add(center, 1, 0);
            earthworksPanel = BuildEarthworksPanel();
            earthworksPanel.Visible = showEarthworks.Checked;
            layout.Controls.Add(earthworksPanel, 2, 0);
            showEarthworks.CheckedChanged += (s, e) =>
            {
                earthworksPanel.Visible = showEarthworks.Checked;
                crossLayout.ColumnStyles[2].Width = showEarthworks.Checked ? (float)(215 * DeviceDpi / 96.0) : 0;
                crossGrid.FitElevenPointColumns();
            };
            crossPage.Controls.Add(layout);
            tips.SetToolTip(sectionStation, "Выберите или введите пикет и нажмите Enter. 6+50 означает 650 м, число 6 означает 6 м. Пикеты сортируются численно. Расстояния поперечника вводятся по съёмке.");
            tips.SetToolTip(leftSlope, "Заложение — горизонталь / вертикаль: для откоса 1:1,5 введите 1,5.");
            tips.SetToolTip(rightSlope, "Правый откос может отличаться от левого. Требуется положительное число.");
            tips.SetToolTip(sectionWater, "Урез общий для этого пикета: изменение переносится в продольный профиль и другие поперечники на том же ПК.");
            tips.SetToolTip(showEarthworks, "Показать параметры снятия ила и рассчитанные площади. Панель изначально скрыта, чтобы в таблице помещалось одиннадцать точек.");
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
            layout.Controls.Add(Note("Все строки выбранного шаблона сохраняются; неизвестные значения остаются пустыми."), 1, 3);
            var explanation = Note(
                "Все исходные расстояния и отметки вводятся в метрах.\n" +
                "Если одна единица чертежа равна метру — введите 1; миллиметру — 0,001.\n\n" +
                "Горизонтальные координаты = расстояние / метров в единице.\n" +
                "Вертикаль = (отметка − ноль) × горизонтальный масштаб / вертикальный масштаб / метров в единице.\n" +
                "Размер текста и строк таблицы согласован с масштабом, как в предоставленных LISP-шаблонах.\n\n" +
                "Факт и берега — чёрный/белый; проект — красный; вода — синий.\n" +
                "Результат — редактируемые объекты AutoCAD.\n" +
                "После закрытия окна кнопкой «Построить» укажите точку вставки в текущей системе координат.\n\n" +
                "Данные проектов сохраняются внутри DWG автоматически после корректного ввода.\n" +
                "После закрытия окна сохраните сам чертёж AutoCAD, чтобы данные остались после перезапуска.\n" +
                "JSON можно импортировать/экспортировать; автозапись JSON включается отдельно.\n" +
                "Площади земляных работ рассчитываются по измеренным поперечникам. Проверяйте полноту съёмки и проектные границы.");
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
            AddColumn(longGrid, "ProjectGround", "Проектная земля, м", "Проектная отметка поверхности для НВК; не отметка проектного дна канала.", 125);
            AddColumn(longGrid, "PipeInvert", "Лоток трубы, м", "Отметка лотка / низа трубы для профиля НВК.", 115);
            AddColumn(longGrid, "PipeDescription", "Труба / изоляция", "Наименование труб, диаметр и изоляция; свободный текст.", 180);
            AddColumn(longGrid, "Foundation", "Основание", "Основание под трубопровод; свободный текст.", 150);
            AddColumn(longGrid, "PointName", "Точка / колодец", "Номер точки, колодца или угла поворота; свободный текст.", 130);
            AddColumn(longGrid, "PeatDepth", "Торф, м", "Глубина торфа для нового канала; необязательное неотрицательное значение.", 95);
            longGrid.Columns["ProjectGround"].DefaultCellStyle.ForeColor = Color.Firebrick;
            longGrid.Columns["PipeInvert"].DefaultCellStyle.ForeColor = Color.Firebrick;
            foreach (string field in new[] { "PipeDescription", "Foundation", "PointName" })
                longGrid.Columns[field].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            longGrid.Columns[0].Frozen = true;
            longGrid.Columns[6].DefaultCellStyle.ForeColor = Color.Firebrick;
            longGrid.Columns[5].DefaultCellStyle.ForeColor = Color.RoyalBlue;
            longGrid.Columns[2].DefaultCellStyle.ForeColor = Color.Black;
            longGrid.Columns[3].DefaultCellStyle.ForeColor = Color.Black;
        }

        private void ConfigureCrossGrid()
        {
            crossGrid.PointsChanged += (s, e) =>
            {
                if (loading) return;
                SetDirty();
                RefreshProjectAnchorDisplay();
                QueueSharedCommit();
            };
            crossGrid.InterpolationRequested += (s, e) => InterpolateCrossCell();
            crossGrid.ProjectAnchorRequested += (s, e) => SetProjectAnchorFromLongitudinal();
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
                RowHeadersWidth = 42,
                ColumnHeadersHeight = 38,
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
            bar.Controls.Add(Button("+ до", () => InsertLongitudinalRow(false)));
            bar.Controls.Add(Button("+ после", () => InsertLongitudinalRow(true)));
            bar.Controls.Add(Button("Вставить из Excel", () => Paste(grid)));
            bar.Controls.Add(Button("Удалить строки", () => DeleteRows(grid)));
            bar.Controls.Add(Button("Взять из чертежа…", () => RequestDrawingCapture(ProfileKind.Longitudinal)));
            bar.Controls.Add(new Label { Text = "Ctrl+V — Excel · Ctrl+C — копировать", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
            var menu = new ContextMenuStrip();
            menu.Items.Add("Вставить строку до", null, (s, e) => InsertLongitudinalRow(false));
            menu.Items.Add("Вставить строку после", null, (s, e) => InsertLongitudinalRow(true));
            menu.Items.Add(new ToolStripSeparator());
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
                var visible = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
                int column = grid.CurrentCell == null ? 0 : visible.FindIndex(c => c.Index == grid.CurrentCell.ColumnIndex);
                if (column < 0) column = 0;
                if (values.Length + row > 10000) throw new InvalidOperationException("Слишком много строк: максимум 10 000.");
                if (values.Any(line => line.Length + column > visible.Count))
                    throw new InvalidOperationException("Вставляемая таблица выходит за последний столбец. Выберите первую ячейку и копируйте только нужные столбцы без заголовков.");
                grid.EndEdit();
                while (grid.Rows.Count - 1 < row + values.Length) grid.Rows.Add();
                for (int y = 0; y < values.Length; y++)
                    for (int x = 0; x < values[y].Length; x++)
                    {
                        grid.Rows[row + y].Cells[visible[column + x].Index].Value = values[y][x].Trim();
                        RecordLongitudinalCell(grid.Rows[row + y].Cells[visible[column + x].Index]);
                    }
                SortLongitudinalRows();
                QueueSharedCommit();
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
            QueueSharedCommit();
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
                    if (!TrySwitchProfileKind(next)) return;
                    SetDirty();
                }
                UpdateModeCaption();
            }
        }

        private void UpdateModeCaption()
        {
            UpdateScopeControls();
            title.Visible = titleCaption.Visible = activeKind == ProfileKind.Longitudinal;
        }

        private ProfileSettings ReadSettings()
        {
            CaptureActiveScales();
            return new ProfileSettings
            {
                Title = title.Text.Trim(),
                HorizontalScale = scaleState.HorizontalScale,
                VerticalScale = scaleState.VerticalScale,
                CrossHorizontalScale = scaleState.CrossHorizontalScale,
                CrossVerticalScale = scaleState.CrossVerticalScale,
                Datum = ReadOptionalNumber(datum, "Ноль профиля"),
                MetresPerDrawingUnit = ReadRequiredNumber(metres, "Метров в единице чертежа"),
                TextHeightMm = ReadRequiredNumber(textHeight, "Высота текста"),
                TextWidthFactor = ReadRequiredNumber(textWidth, "Коэффициент ширины текста"),
                ShowEmptyRows = true
            };
        }

        private List<LongitudinalPoint> ReadLongitudinal()
        {
            longGrid.EndEdit();
            SortLongitudinalRows();
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
                    LeftSlope = Optional(row, "LeftSlope"), RightSlope = Optional(row, "RightSlope"),
                    ProjectGround = Optional(row, "ProjectGround"), PipeInvert = Optional(row, "PipeInvert"),
                    PipeDescription = StringCell(row, "PipeDescription"), Foundation = StringCell(row, "Foundation"),
                    PointName = StringCell(row, "PointName"), PeatDepth = Optional(row, "PeatDepth")
                });
            }
            return result;
        }

        private static string StringCell(DataGridViewRow row, string column) => Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture)?.Trim() ?? "";

        private static bool RowEmpty(DataGridViewRow row)
        {
            return row.Cells.Cast<DataGridViewCell>().All(c => string.IsNullOrWhiteSpace(Convert.ToString(c.Value, CultureInfo.InvariantCulture)));
        }

        private static double Required(DataGridViewRow row, string column)
        {
            var cell = row.Cells[column];
            if (cell.Value is double number) return number;
            return NumberParser.ParseRequired(Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                "Строка " + (row.Index + 1) + ", «" + cell.OwningColumn.HeaderText + "»");
        }

        private static double? Optional(DataGridViewRow row, string column)
        {
            var cell = row.Cells[column];
            if (cell.Value is double number) return number;
            return NumberParser.ParseOptional(Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                "Строка " + (row.Index + 1) + ", «" + cell.OwningColumn.HeaderText + "»");
        }

        private CrossSection ReadCurrentSection()
        {
            var points = crossGrid.ReadPoints();
            double? station = ReadSectionStation();
            var previous = selectedSection >= 0 && selectedSection < sections.Count ? sections[selectedSection] : null;
            var leftBankPoint = points.FirstOrDefault(p => p.Role == SectionPointRole.LeftBank);
            var bottomPoint = points.FirstOrDefault(p => p.Role == SectionPointRole.Bottom);
            var rightBankPoint = points.FirstOrDefault(p => p.Role == SectionPointRole.RightBank);
            var result = new CrossSection
            {
                SectionId = previous?.SectionId ?? Guid.NewGuid().ToString("N"),
                Name = station.HasValue ? StationCaption(station.Value) : sectionName.Text.Trim(),
                Station = station,
                LeftBank = leftBankPoint == null ? previous?.LeftBank : leftBankPoint.Existing,
                ExistingBottom = bottomPoint == null ? previous?.ExistingBottom : bottomPoint.Existing,
                RightBank = rightBankPoint == null ? previous?.RightBank : rightBankPoint.Existing,
                Water = ReadOptionalNumber(sectionWater, "Урез воды поперечника"),
                ProjectBottom = ReadOptionalNumber(projectBottom, "Проектное дно поперечника"),
                BottomWidth = ReadOptionalNumber(bottomWidth, "Ширина проектного дна"),
                LeftSlope = ReadOptionalNumber(leftSlope, "Левый откос"),
                RightSlope = ReadOptionalNumber(rightSlope, "Правый откос"),
                CenterOffset = ReadOptionalNumber(centerOffset, "Центр проектного дна") ?? 0,
                UsePointDistances = true,
                ProjectAnchorIndex = crossGrid.ProjectAnchorIndex,
                UseLongitudinalProjectBottom = longitudinalProjectBottomLinked,
                UseLongitudinalWater = longitudinalWaterLinked,
                DesignMode = DesignModeForSave,
                Points = points
            };
            ReadEarthworks(result);
            return result;
        }

        private void CommitSection()
        {
            if (selectedSection < 0 || selectedSection >= sections.Count) return;
            CommitSynchronizedSection();
        }

        private void SectionSelectionChanged(object sender, EventArgs e)
        {
            if (loading || sectionList.SelectedIndex == selectedSection) return;
            int next = sectionList.SelectedIndex;
            requestedSectionSelection = next >= 0 && next < sections.Count ? sections[next] : null;
            try { CommitStationInput(); CommitSection(); }
            catch (Exception ex)
            {
                loading = true;
                sectionList.SelectedIndex = selectedSection;
                loading = false;
                requestedSectionSelection = null;
                ShowError(ex.Message);
                return;
            }
            selectedSection = requestedSectionSelection == null ? -1 : sections.IndexOf(requestedSectionSelection);
            requestedSectionSelection = null;
            RefreshSectionList(selectedSection);
            DisplaySection();
        }

        private void DisplaySection()
        {
            bool old = loading;
            loading = true;
            try
            {
                var section = selectedSection >= 0 && selectedSection < sections.Count ? CloneSection(sections[selectedSection]) : NewSection(1);
                currentSectionStation = section.Station;
                stationInputEdited = false;
                sectionStation.Text = section.Station.HasValue ? StationCaption(section.Station.Value) : "";
                sectionName.Text = section.Station.HasValue ? StationCaption(section.Station.Value) : section.Name ?? "";
                sectionName.ReadOnly = section.Station.HasValue;
                longitudinalProjectBottomLinked = section.UseLongitudinalProjectBottom;
                longitudinalWaterLinked = section.UseLongitudinalWater;
                DisplayNumber(sectionWater, section.Water);
                DisplayNumber(projectBottom, section.ProjectBottom);
                DisplayNumber(bottomWidth, section.BottomWidth);
                DisplayNumber(leftSlope, section.LeftSlope);
                DisplayNumber(rightSlope, section.RightSlope);
                DisplayNumber(centerOffset, section.CenterOffset);
                SelectDesignMode(section.DesignMode, ProfileInterpolation.EffectiveDesignMode(section));
                crossGrid.LoadPoints(ToDistancePoints(section));
                crossGrid.SetProjectAnchor(section.ProjectAnchorIndex, section.ProjectBottom);
                UpdateProjectDisplayIndex(section, !section.ProjectBottom.HasValue);
                LoadEarthworks(section);
                RefreshProjectAnchorDisplay();
            }
            finally { loading = old; }
            RefreshLinkedSectionValues();
            RefreshEarthworks();
            sectionBeforeEditing = ReadCurrentSection();
            UpdateModeCaption();
        }

        private void RefreshSectionList(int selection)
        {
            bool old = loading;
            loading = true;
            sectionList.Items.Clear();
            for (int i = 0; i < sections.Count; i++)
                sectionList.Items.Add((i + 1) + ". " + SectionCaption(sections[i]));
            sectionList.SelectedIndex = selection >= 0 && selection < sections.Count ? selection : -1;
            loading = old;
        }

        private void AddSection()
        {
            try
            {
                CommitStationInput();
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
                CommitStationInput();
                CommitSection();
                if (selectedSection < 0) return;
                var source = sections[selectedSection];
                var copy = CloneSection(source);
                copy.SectionId = Guid.NewGuid().ToString("N");
                copy.Name = SectionCaption(source) + " — копия";
                copy.Station = null;
                copy.UseLongitudinalProjectBottom = false;
                copy.UseLongitudinalWater = false;
                foreach (var point in copy.Points) point.UseLongitudinalElevation = false;
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
            pendingSharedValues.Clear();
            if (sections.Count == 0) sections.Add(NewSection(1));
            selectedSection = Math.Min(selectedSection, sections.Count - 1);
            RefreshSectionList(selectedSection);
            DisplaySection();
            SetDirty();
        }

        private ProfileProject ReadProject()
        {
            Validate();
            CommitStationInput();
            CommitSection();
            var project = CloneProfileContainer();
            project.ProjectId = currentProjectId;
            project.Name = currentProjectName;
            project.Template = SelectedTemplate;
            project.Kind = activeKind;
            project.Settings = ReadSettings();
            project.Longitudinal = ReadLongitudinal();
            project.Sections = sections.Select(CloneSection).ToList();
            return project;
        }

        private void LoadProject(ProfileProject project)
        {
            loading = true;
            try
            {
                profileContainer = ProjectFile.Clone(project);
                pendingSharedValues.Clear();
                pendingLongitudinalCells.Clear();
                sectionBeforeEditing = null;
                requestedSectionSelection = null;
                currentProjectId = string.IsNullOrWhiteSpace(project.ProjectId) ? Guid.NewGuid().ToString("N") : project.ProjectId;
                currentProjectName = string.IsNullOrWhiteSpace(project.Name) ? "Проект" : project.Name;
                activeKind = project.Kind;
                RefreshProfileTabs();
                SelectTemplate(project.Template);
                var settings = project.Settings ?? new ProfileSettings();
                title.Text = settings.Title ?? "";
                LoadProfileScales(settings);
                DisplayNumber(datum, settings.Datum);
                DisplayNumber(metres, settings.MetresPerDrawingUnit);
                DisplayNumber(textHeight, settings.TextHeightMm);
                DisplayNumber(textWidth, settings.TextWidthFactor);
                longGrid.Rows.Clear();
                foreach (var point in project.Longitudinal ?? new List<LongitudinalPoint>())
                    longGrid.Rows.Add(point.Station, point.Ground, point.LeftBank, point.RightBank,
                        point.ExistingBottom, point.Water, point.ProjectBottom, point.BottomWidth, point.LeftSlope, point.RightSlope,
                        point.ProjectGround, point.PipeInvert, point.PipeDescription, point.Foundation, point.PointName, point.PeatDepth);
                SortLongitudinalRows();
                sharedLongitudinalBaseline = CopyLongitudinal(ReadLongitudinal());
                sections.Clear();
                sections.AddRange((project.Sections ?? new List<CrossSection>()).Select(CloneSection));
                foreach (var section in sections)
                    if (section.Station.HasValue) section.Name = StationCaption(section.Station.Value);
                if (sections.Count == 0) sections.Add(NewSection(1));
                selectedSection = 0;
                SortSections(sections.OrderBy(section => section.Station.HasValue ? 0 : 1).ThenBy(section => section.Station ?? 0).First());
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
            RegisterProject(project);
            RefreshProjectPicker();
            RestoreJsonSelection();
            RecalculateDerivedValues();
            UpdateTitle();
        }

        private void RefreshPreview(bool switchTab)
        {
            try
            {
                var project = ReadProject();
                var plan = BuildScopedPlan(project);
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

        private void Draw(bool rebuild)
        {
            try
            {
                var project = ReadProject();
                var plan = BuildScopedPlan(project);
                preview.SetPlan(plan);
                previewNotes.Text = plan.Warnings.Count == 0 ? "Проверка завершена без замечаний." : string.Join(Environment.NewLine, plan.Warnings);
                if (plan.Warnings.Count > 0 && MessageBox.Show(this,
                    string.Join(Environment.NewLine, plan.Warnings.Take(12)) +
                    (plan.Warnings.Count > 12 ? "\n\nЕщё замечаний: " + (plan.Warnings.Count - 12) + ". Полный список находится на вкладке «Предпросмотр»." : "") + "\n\nПродолжить построение?",
                    "Замечания к профилю", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                PersistProject(project, true);
                Project = project;
                Plan = plan;
                RebuildExisting = rebuild;
                accepted = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void WireChanges(Control control)
        {
            if (control is TextBox box && !box.ReadOnly) box.TextChanged += (s, e) =>
            {
                MarkNumberEdited(box);
                SetDirty();
            };
            if (control == sectionStation) sectionStation.TextChanged += (s, e) => SetDirty();
            if (control is CheckBox check && check != showEarthworks) check.CheckedChanged += (s, e) => SetDirty();
            if (control is DataGridView grid && grid != crossGrid)
            {
                grid.CellValueChanged += (s, e) => SetDirty();
                grid.RowsRemoved += (s, e) => SetDirty();
                grid.UserAddedRow += (s, e) => SetDirty();
            }
            foreach (Control child in control.Controls) WireChanges(child);
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

        private static CrossSection NewSection(int number) => new CrossSection
        {
            Name = "Поперечник " + number, UsePointDistances = true, DesignMode = SectionDesignMode.Automatic,
            Points = new List<SectionPoint>
            {
                new SectionPoint { Role = SectionPointRole.LeftBank, DistanceFromPrevious = 0 },
                new SectionPoint { Role = SectionPointRole.Bottom },
                new SectionPoint { Role = SectionPointRole.RightBank }
            }
        };

        private static CrossSection CloneSection(CrossSection value)
        {
            return ProfileInterpolation.CloneSection(value);
        }

        private static TextBox Input() => new TextBox { Width = 82, Margin = new Padding(2, 3, 6, 3) };

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 4, 4, 4) };

        private static Label Note(string text) => new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = new Padding(2, 3, 2, 5) };

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(text.Length < 3 ? 30 : 65, 26), Padding = new Padding(3, 1, 3, 1), Margin = new Padding(2) };
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
            if (disposing)
            {
                tips.Dispose();
                autoSaveTimer.Dispose();
                interpolationTimer.Dispose();
                interpolationFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // DataGridView's editing textbox otherwise consumes Ctrl+V and puts
            // an entire Excel range into one cell instead of distributing it.
            if (keyData == (Keys.Control | Keys.V))
            {
                if (longGrid.ContainsFocus) { Paste(longGrid); return true; }
                if (crossGrid.ContainsFocus) { RunPointAction(crossGrid.PasteTable); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
