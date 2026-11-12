using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Drawing settings: scale and sizes of the point signs. Empty fields stay empty
    /// until the user fills them: the module substitutes no normative value.
    /// </summary>
    internal sealed class SettingsView : UserControl, IPanelView
    {
        private readonly ComboBox scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox labelHeight = new TextBox();
        private readonly TextBox pointSize = new TextBox();
        private readonly NumericUpDown decimals = new NumericUpDown { Minimum = 0, Maximum = 4, Width = 60 };
        private readonly Label problems = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.Firebrick };
        private KzmPanel panel;
        private KzmSettings loaded;

        internal SettingsView()
        {
            AutoScroll = true;
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var header = new Label { Text = "Оформление точек", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 9, 3, 3) };
            table.Controls.Add(header);
            table.SetColumnSpan(header, 2);
            scale.Items.Add("— не задан —");
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            Row(table, "Масштаб", scale);
            Row(table, "Высота подписи отметки, мм на листе", labelHeight);
            Row(table, "Размер знака точки, мм на листе", pointSize);
            Row(table, "Знаков после запятой", decimals);
            table.Controls.Add(problems);
            table.SetColumnSpan(problems, 2);

            var save = new Button { Text = "Сохранить в чертеже", AutoSize = true };
            save.Click += (s, e) => Save();
            var reset = new Button { Text = "Вернуть сохранённые", AutoSize = true };
            reset.Click += (s, e) => Display(loaded);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(save);
            buttons.Controls.Add(reset);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(330, 0),
                Text = "Размеры задаются в мм листа и умножаются на масштаб (1:500 → 0,5 м на 1 мм). " +
                       "При изменении масштаба, размеров или числа знаков существующие точки перестраиваются.",
            };
            table.Controls.Add(note);
            table.SetColumnSpan(note, 2);
            Controls.Add(table);
        }

        private static void Row(TableLayoutPanel table, string caption, Control control)
        {
            table.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(170, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) });
            if (control is TextBox) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            table.Controls.Add(control);
        }

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            loaded = panel.Snapshot.Settings.Clone();
            Display(loaded);
            var missing = loaded.ValidateDrawing();
            problems.Text = missing.Count > 0 ? "Не задано: " + string.Join("; ", missing) + "." : string.Empty;
        }

        private void Display(KzmSettings settings)
        {
            if (settings == null) return;
            scale.SelectedIndex = settings.ScaleDenominator.HasValue
                ? Math.Max(0, Array.IndexOf(KzmSettings.Scales, settings.ScaleDenominator.Value) + 1) : 0;
            labelHeight.Text = FormatValue(settings.LabelHeightMm);
            pointSize.Text = FormatValue(settings.PointSizeMm);
            decimals.Value = Math.Max(0, Math.Min(4, settings.Decimals));
        }

        internal static string FormatValue(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',') : string.Empty;

        private static double? Parse(string text, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (ElevationFormat.TryParse(text, out double value)) return value;
            errors.Add(name + ": введите число");
            return null;
        }

        private void Save()
        {
            if (panel?.Document == null) return;
            var errors = new List<string>();
            var settings = loaded.Clone();
            settings.ScaleDenominator = scale.SelectedIndex > 0 ? KzmSettings.Scales[scale.SelectedIndex - 1] : (int?)null;
            settings.LabelHeightMm = Parse(labelHeight.Text, "высота подписи", errors);
            settings.PointSizeMm = Parse(pointSize.Text, "размер знака", errors);
            settings.Decimals = (int)decimals.Value;
            errors.AddRange(settings.ValidateDrawing().Where(problem => !problem.StartsWith("не задан", StringComparison.Ordinal)));
            if (errors.Count > 0)
            {
                problems.Text = "Исправьте: " + string.Join("; ", errors.Distinct()) + ".";
                return;
            }
            var previous = loaded;
            PanelActions.Run("настройки", document =>
            {
                int resized = EditServices.SaveSettings(document.Database, previous, settings);
                PanelMessages.Info("Настройки сохранены в чертеже." + (resized > 0 ? " Перестроено точек: " + resized + "." : string.Empty));
            }, () =>
            {
                panel.SettingsSaved();
                panel.RefreshAll();
            });
        }
    }
}
