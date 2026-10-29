using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private readonly ComboBox approximationShape = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private Button fillCrossElevationsButton;
        private Button crossBankWidthButton;

        private Control BuildCrossApproximationPanel()
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            approximationShape.Items.AddRange(new object[] { "Плавная чаша", "Прямые откосы" });
            approximationShape.SelectedIndex = 0;
            approximationShape.Width = Px(132);
            tips.SetToolTip(approximationShape, "Плавная чаша: небольшой подъём у дна, без выбросов между опорами. Прямые откосы: линейный расчёт. Ручные отметки сохраняются.");
            crossBankWidthButton = Button("Ширина между\nберегами…", ShowCrossBankWidth);
            fillCrossElevationsButton = Button("Заполнить\nотметки", () => RunPointAction(FillCrossElevations));
            foreach (var button in new[] { crossBankWidthButton, fillCrossElevationsButton })
            {
                button.AutoSize = false;
                button.Size = new Size(Px(132), Px(42));
                button.Margin = new Padding(0, 3, 0, 3);
            }
            tips.SetToolTip(crossBankWidthButton, "Задайте ширину между назначенными берегами и положение существующего дна. Распределите пустые интервалы или разрешите перераспределение всех интервалов между берегами.");
            tips.SetToolTip(fillCrossElevationsButton, "Заполнить пустые фактические отметки по опорам и расстояниям. Сначала задайте ширину или интервалы. За крайними опорами используется линейное продолжение.");
            panel.Controls.Add(crossBankWidthButton);
            panel.Controls.Add(approximationShape);
            panel.Controls.Add(fillCrossElevationsButton);
            var note = Note("Приблизительный расчёт.\nПроверьте по съёмке.");
            note.MaximumSize = new Size(Px(132), 0);
            panel.Controls.Add(note);
            return panel;
        }

        private void FillCrossElevations()
        {
            CommitStationInput();
            CommitSection();
            var current = ReadCurrentSection();
            int empty = current.Points.Count(point => !point.Existing.HasValue);
            var result = SectionApproximation.FillElevations(current,
                approximationShape.SelectedIndex == 1 ? SectionApproximationShape.StraightSlopes : SectionApproximationShape.SmoothBowl,
                ReadLongitudinal());
            ApplyCrossApproximation(result);
            SetStatus("Расчётно заполнено отметок: " + empty + ". Ручные опоры сохранены. Проверьте результат по съёмке; для изменения формы очистите нужные расчётные ячейки и заполните снова.");
        }

        private void ApplyCrossApproximation(CrossSection result)
        {
            int point = crossGrid.SelectedPointIndex;
            int row = (int)crossGrid.SelectedRowKind;
            var project = SynchronizationSource(ReadLongitudinal());
            project.Sections[selectedSection] = result;
            ApplySynchronizedProject(project, selectedSection);
            if (point >= 0 && point < result.Points.Count)
                crossGrid.CurrentCell = crossGrid.Rows[row].Cells[point + 1];
            SetDirty();
        }

        private void DistributeCrossDistances(double width, double? bottomPosition, bool preserve)
        {
            CommitStationInput();
            CommitSection();
            ApplyCrossApproximation(SectionApproximation.DistributeDistances(ReadCurrentSection(), width, bottomPosition, preserve));
            SetStatus("Расстояния распределены: между берегами " + Format(width) + " м. Отметки заполняются отдельной кнопкой. Расчётные интервалы проверьте по съёмке.");
        }

        private void ShowCrossBankWidth()
        {
            try
            {
                CommitStationInput();
                CommitSection();
                using (var dialog = new Form
                {
                    Text = "Ширина между берегами", StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                    ShowInTaskbar = false, Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
                    ClientSize = new Size(Px(580), Px(330))
                })
                {
                    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 6 };
                    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                    var explanation = new Label { AutoSize = true, MaximumSize = new Size(Px(540), 0), Text = "Ширина — горизонтальное расстояние от левого до правого берега.\nПоложение дна можно сместить для несимметричного русла." };
                    layout.Controls.Add(explanation, 0, 0); layout.SetColumnSpan(explanation, 2);
                    var width = new TextBox { Dock = DockStyle.Top, AccessibleName = "Ширина между берегами, м" };
                    var bottom = new TextBox { Dock = DockStyle.Top, AccessibleName = "Дно от левого берега, м" };
                    layout.Controls.Add(new Label { Text = "Ширина между берегами, м", AutoSize = true }, 0, 1);
                    layout.Controls.Add(width, 1, 1);
                    layout.Controls.Add(new Label { Text = "Дно от левого берега, м\nПусто — середина ширины", AutoSize = true }, 0, 2);
                    layout.Controls.Add(bottom, 1, 2);
                    var preserve = new CheckBox { Text = "Сохранить заполненные интервалы", Checked = true, AutoSize = true };
                    layout.Controls.Add(preserve, 0, 3); layout.SetColumnSpan(preserve, 2);
                    var error = new Label { AutoSize = true, MaximumSize = new Size(Px(540), 0), Text = "Остаток каждой стороны делится поровну между пустыми интервалами.\nПустые интервалы за берегами получают средний шаг своей стороны.\nГотовые отметки не изменяются при перераспределении расстояний." };
                    layout.Controls.Add(error, 0, 4); layout.SetColumnSpan(error, 2);
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                    var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
                    var apply = new Button { Text = "Распределить", AutoSize = true };
                    double requestedWidth = 0;
                    apply.Click += (sender, args) =>
                    {
                        try
                        {
                            requestedWidth = NumberParser.ParseRequired(width.Text, "Ширина между берегами");
                            DistributeCrossDistances(requestedWidth,
                                NumberParser.ParseOptional(bottom.Text, "Дно от левого берега"), preserve.Checked);
                            dialog.DialogResult = DialogResult.OK;
                        }
                        catch (ProfileValidationException ex) { error.ForeColor = Color.Firebrick; error.Text = ex.Message; }
                    };
                    buttons.Controls.Add(cancel); buttons.Controls.Add(apply);
                    layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
                    dialog.Controls.Add(layout); dialog.AcceptButton = apply; dialog.CancelButton = cancel;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                }
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }
}
