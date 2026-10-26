using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private bool sectionNameEdited;

        private void InitializeStationAssignment()
        {
            tips.SetToolTip(sectionName, "Название или пикет: 0+20, 20, ПК 0+20. Enter или выход из поля назначает промер на продольный профиль.");
            sectionName.TextChanged += (s, e) => { if (!loading) sectionNameEdited = true; };
            sectionName.Validated += (s, e) =>
            {
                if (loading) return;
                try { CommitSectionName(); }
                catch (Exception error) { SetStatus("Промер не назначен: " + error.Message, true); }
            };
            sectionName.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                try { CommitSectionName(); }
                catch (Exception error) { ShowError(error.Message); }
            };
        }

        private void CommitSectionName()
        {
            if (loading || stationChangeActive || !sectionNameEdited) return;
            if (!SectionStationAssignment.TryParseName(sectionName.Text, out var station))
            {
                sectionNameEdited = false;
                CommitSection();
                return;
            }
            AssignCurrentSection(station);
        }

        private void AssignCurrentSection(double station)
        {
            if (loading || stationChangeActive) return;
            stationChangeActive = true;
            try
            {
                // Flush real edits under the old station before moving the measured section.
                CommitSection();
                var source = SynchronizationSource(ReadLongitudinal());
                var conflicts = SectionStationAssignment.ConflictingFields(source, selectedSection, station);
                if (conflicts.Count > 0 && MessageBox.Show(this,
                    StationCaption(station) + ": заменить данными промера следующие значения?\n\n" +
                    string.Join(", ", conflicts) + "\n\nРасстояния и точки промера сохранятся. Остальные поля пикета не изменятся.",
                    "Назначить промер", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    DisplaySection();
                    return;
                }
                ApplySynchronizedProject(SectionStationAssignment.Assign(source, selectedSection, station), selectedSection);
                sectionNameEdited = false;
                stationInputEdited = false;
                SetDirty();
                SetStatus("Промер назначен на " + StationCaption(station) + ". Отметки перенесены в продольную таблицу. Для обновления чертежа нажмите «Перестроить текущий» на вкладке продольного профиля.");
            }
            finally { stationChangeActive = false; }
        }

        private void ShowStationAssignment()
        {
            try
            {
                CommitStationInput();
                CommitSection();
                using (var dialog = new Form
                {
                    Text = "Назначить на продольный профиль", StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                    ClientSize = new Size(Px(510), Px(175)), Font = Font, ShowInTaskbar = false
                })
                {
                    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
                    layout.Controls.Add(new Label { Text = "Выберите готовый пикет или введите новый (0+20 = 20 м).\nОтметки выбранного промера появятся на продольном профиле.", AutoSize = true }, 0, 0);
                    var picker = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(0, 12, 0, 8) };
                    foreach (var point in ReadLongitudinal()) picker.Items.Add(new StationChoice { Value = point.Station });
                    picker.SelectedItem = picker.Items.OfType<StationChoice>().FirstOrDefault(choice => choice.Value == currentSectionStation);
                    if (picker.SelectedItem == null) picker.Text = currentSectionStation.HasValue ? Format(currentSectionStation.Value) : "";
                    bool typed = false;
                    picker.TextUpdate += (s, e) => typed = true;
                    picker.SelectionChangeCommitted += (s, e) => typed = false;
                    layout.Controls.Add(picker, 0, 1);
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                    var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
                    var assign = new Button { Text = "Назначить", AutoSize = true };
                    double target = 0;
                    assign.Click += (s, e) =>
                    {
                        if (!typed && picker.SelectedItem is StationChoice choice) target = choice.Value;
                        else if (!SectionStationAssignment.TryParseName(picker.Text, out target))
                        { MessageBox.Show(dialog, "Введите пикет: например 0+20 или 20.", "Пикет", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                        dialog.DialogResult = DialogResult.OK;
                    };
                    buttons.Controls.Add(cancel); buttons.Controls.Add(assign);
                    layout.Controls.Add(buttons, 0, 2);
                    dialog.Controls.Add(layout);
                    dialog.AcceptButton = assign; dialog.CancelButton = cancel;
                    if (dialog.ShowDialog(this) == DialogResult.OK) AssignCurrentSection(target);
                }
            }
            catch (Exception error) { ShowError(error.Message); }
        }
    }
}
