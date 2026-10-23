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
        private readonly ComboBox designMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 205, Margin = new Padding(3, 4, 12, 4) };
        private readonly TextBox siltThickness = Input();
        private readonly TextBox siltLeft = Input();
        private readonly TextBox siltRight = Input();
        private readonly Label cutArea = QuantityLabel();
        private readonly Label fillArea = QuantityLabel();
        private readonly Label siltArea = QuantityLabel();
        private readonly Label earthworksStatus = Note("");
        private readonly Label surveyOrigin = Note("");
        private bool longitudinalProjectBottomLinked;
        private bool longitudinalWaterLinked;

        private sealed class DesignChoice
        {
            internal SectionDesignMode Value;
            internal string Name;
            public override string ToString() => Name;
        }

        private SectionDesignMode SelectedDesignMode => designMode.SelectedItem is DesignChoice item ? item.Value : SectionDesignMode.None;

        private Control BuildCrossDesignPanel()
        {
            designMode.Items.Add(new DesignChoice { Value = SectionDesignMode.None, Name = "Без проекта" });
            designMode.Items.Add(new DesignChoice { Value = SectionDesignMode.Trapezoid, Name = "Трапеция" });
            designMode.Items.Add(new DesignChoice { Value = SectionDesignMode.Points, Name = "По проектным отметкам" });
            designMode.SelectedIndex = 0;
            designMode.SelectedIndexChanged += (s, e) =>
            {
                RefreshProjectAnchorDisplay();
                if (!loading) SetDirty();
            };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 3 };
            panel.Controls.Add(Fields("Проект", designMode, "Дно, м", projectBottom), 0, 0);
            panel.Controls.Add(Fields("Ширина дна, м", bottomWidth, "Откос слева 1:", leftSlope, "справа 1:", rightSlope), 0, 1);
            var center = (FlowLayoutPanel)Fields("Центр дна, м", centerOffset);
            center.Controls.Add(Button("Центр по фактическому дну", FindAutoCenter));
            panel.Controls.Add(center, 0, 2);
            tips.SetToolTip(projectBottom, "Отметка дна проектной трапеции. Кнопка под таблицей связывает её с продольным профилем под выбранной точкой.");
            tips.SetToolTip(centerOffset, "Смещение центра проектной трапеции в системе поперечника. Ручной ввод снимает привязку к выбранному столбцу.");
            tips.SetToolTip(designMode, "Смена способа сохраняет неактивные исходные параметры. В режиме «По проектным отметкам» заполняется красная строка таблицы.");
            return panel;
        }

        private Control BuildPointToolbar()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 5, 0, 0) };
            bar.Controls.Add(Button("+ слева", () => RunPointAction(crossGrid.AddPointBefore)));
            bar.Controls.Add(Button("+ справа", () => RunPointAction(crossGrid.AddPointAfter)));
            bar.Controls.Add(Button("Удалить точку", () =>
            {
                if (crossGrid.SelectedPointIndex < 0) { SetStatus("Выберите столбец точки.", true); return; }
                if (MessageBox.Show(this, "Удалить выбранную точку? Расстояние через удалённую точку потребуется проверить и ввести заново.", "Удаление точки", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RunPointAction(crossGrid.RemoveSelectedPoint);
            }));
            bar.Controls.Add(Button("Вставить из Excel", () => RunPointAction(crossGrid.PasteTable)));
            bar.Controls.Add(Button("Проектное дно из продольного", SetProjectAnchorFromLongitudinal));
            return bar;
        }

        private void RunPointAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void SelectDesignMode(SectionDesignMode mode)
        {
            for (int index = 0; index < designMode.Items.Count; index++)
                if (((DesignChoice)designMode.Items[index]).Value == mode) { designMode.SelectedIndex = index; return; }
            designMode.SelectedIndex = 0;
        }

        private void RefreshProjectAnchorDisplay()
        {
            bool trapezoid = SelectedDesignMode == SectionDesignMode.Trapezoid;
            projectBottom.Enabled = bottomWidth.Enabled = leftSlope.Enabled = rightSlope.Enabled = centerOffset.Enabled = trapezoid;
            crossGrid.ManualDesignEnabled = SelectedDesignMode == SectionDesignMode.Points;
            crossGrid.ProjectAnchorVisible = trapezoid;
            double? bottom = null;
            try { bottom = ReadOptionalNumber(projectBottom, "Проектное дно"); }
            catch (ProfileValidationException) { }
            crossGrid.SetProjectAnchor(crossGrid.ProjectAnchorIndex, bottom);
        }

        private void SetProjectAnchorFromLongitudinal()
        {
            try
            {
                CommitStationInput();
                int index = crossGrid.SelectedPointIndex;
                if (index < 0) throw new ProfileValidationException("Выберите ячейку в столбце точки, под которой должно находиться проектное дно.");
                if (!currentSectionStation.HasValue) throw new ProfileValidationException("Сначала выберите или введите пикет поперечника.");
                var sample = ProfileInterpolation.SampleLongitudinal(ReadLongitudinal(), currentSectionStation.Value);
                if (!sample.ProjectBottom.HasValue) throw new ProfileValidationException("На этом пикете нет проектной отметки дна. Задайте её в продольном профиле или введите вручную в поле «Дно».");
                DisplayNumber(projectBottom, sample.ProjectBottom);
                longitudinalProjectBottomLinked = true;
                crossGrid.SetProjectAnchor(index, sample.ProjectBottom);
                SelectDesignMode(SectionDesignMode.Trapezoid);
                try
                {
                    var resolved = ProfileInterpolation.ResolveSection(ReadCurrentSection(), ReadLongitudinal());
                    DisplayNumber(centerOffset, resolved.CenterOffset);
                }
                catch (ProfileValidationException) { /* An anchor can be saved before survey distances exist. */ }
                SetDirty();
                SetStatus("Проектное дно связано с продольным профилем под точкой " + (index + 1) + ". Для трапеции задайте ширину и оба откоса; расстояния должны быть известны.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private static List<SectionPoint> ToDistancePoints(CrossSection section)
        {
            var points = ProfileInterpolation.CloneSection(section).Points;
            if (points.Count == 0 && (section.LeftBank.HasValue || section.ExistingBottom.HasValue || section.RightBank.HasValue))
                return new List<SectionPoint>
                {
                    new SectionPoint { Role = SectionPointRole.LeftBank, Existing = section.LeftBank, DistanceFromPrevious = 0 },
                    new SectionPoint { Role = SectionPointRole.Bottom, Existing = section.ExistingBottom },
                    new SectionPoint { Role = SectionPointRole.RightBank, Existing = section.RightBank }
                };
            if (!section.UsePointDistances)
                for (int index = 0; index < points.Count; index++)
                    points[index].DistanceFromPrevious = index == 0 ? 0 : points[index].Offset - points[index - 1].Offset;
            return points;
        }

        private Control BuildEarthworksPanel()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(3) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label { Text = "Снятие ила", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 8) });
            content.Controls.Add(Caption("Толщина по вертикали, м"));
            content.Controls.Add(siltThickness);
            content.Controls.Add(Caption("Левая граница, м"));
            content.Controls.Add(siltLeft);
            content.Controls.Add(Caption("Правая граница, м"));
            content.Controls.Add(siltRight);
            content.Controls.Add(Note("Толщина пустая — без снятия ила. Границы пустые — по ширине проекта; без проекта — по измеренному участку."));
            content.Controls.Add(surveyOrigin);
            content.Controls.Add(new Label { Text = "Площади поперечника", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 12, 3, 8) });
            content.Controls.Add(Caption("Выемка после снятия ила"));
            content.Controls.Add(cutArea);
            content.Controls.Add(Caption("Насыпь"));
            content.Controls.Add(fillArea);
            content.Controls.Add(Caption("Снятие ила"));
            content.Controls.Add(siltArea);
            content.Controls.Add(earthworksStatus);
            content.Controls.Add(Button("Обновить расчёт", RefreshEarthworks));
            scroll.Controls.Add(content);
            foreach (Control control in content.Controls)
                if (control is Label label) label.MaximumSize = new Size(215, 0);
            tips.SetToolTip(siltLeft, "Граница в системе смещений поперечника. Для нового поперечника первая точка имеет смещение 0. Импортированные абсолютные смещения сохраняются.");
            tips.SetToolTip(siltRight, "Весь диапазон снятия ила должен быть покрыт фактическими отметками без пропусков.");
            return scroll;
        }

        private static Label QuantityLabel() => new Label { Text = "—", AutoSize = true, ForeColor = Color.FromArgb(35, 65, 85), Margin = new Padding(3, 0, 3, 8) };

        private void ReadEarthworks(CrossSection section)
        {
            section.SiltThickness = ReadOptionalNumber(siltThickness, "Толщина снимаемого ила");
            section.SiltLeft = ReadOptionalNumber(siltLeft, "Левая граница снятия ила");
            section.SiltRight = ReadOptionalNumber(siltRight, "Правая граница снятия ила");
        }

        private void LoadEarthworks(CrossSection section)
        {
            DisplayNumber(siltThickness, section.SiltThickness);
            DisplayNumber(siltLeft, section.SiltLeft);
            DisplayNumber(siltRight, section.SiltRight);
            UpdateSurveyOrigin(section);
        }

        private void UpdateSurveyOrigin(CrossSection section)
        {
            double origin = section.Points.Count == 0 ? 0 : section.Points[0].Offset;
            surveyOrigin.Text = "Границы и центр — смещения в системе съёмки. Первая точка: " + origin.ToString("F2", CultureInfo.InvariantCulture) + " м.";
        }

        private void RefreshEarthworks()
        {
            if (longGrid.IsCurrentCellInEditMode || crossGrid.IsCurrentCellInEditMode) return;
            try
            {
                var section = ReadCurrentSection();
                UpdateSurveyOrigin(section);
                var calculation = Earthworks.Calculate(section, ReadLongitudinal());
                cutArea.Text = AreaText(calculation.CutArea);
                fillArea.Text = AreaText(calculation.FillArea);
                siltArea.Text = AreaText(calculation.SiltArea);
                earthworksStatus.Text = calculation.Status;
                earthworksStatus.ForeColor = calculation.IsValid ? Color.DimGray : Color.DarkOrange;
            }
            catch (Exception ex)
            {
                cutArea.Text = fillArea.Text = siltArea.Text = "—";
                earthworksStatus.Text = ex.Message;
                earthworksStatus.ForeColor = Color.DarkOrange;
            }
        }

        private static string AreaText(double? value) => value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + " м²" : "—";
    }
}
