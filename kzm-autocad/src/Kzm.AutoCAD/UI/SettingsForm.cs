using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.Core;

namespace Kzm.AutoCAD.UI
{
    /// <summary>
    /// Settings of stage 1. Every value is visible; empty fields stay empty until
    /// the user fills them (no normative value is substituted by the module).
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly KzmSettings source;
        private readonly bool requireComplete;
        private readonly TextBox pattern = new TextBox();
        private readonly TextBox sample = new TextBox();
        private readonly Label sampleResult = new Label();
        private readonly TextBox min = new TextBox();
        private readonly TextBox max = new TextBox();
        private readonly TextBox radius = new TextBox();
        private readonly TextBox tolerance = new TextBox();
        private readonly ComboBox scale = new ComboBox();
        private readonly TextBox labelHeight = new TextBox();
        private readonly TextBox pointSize = new TextBox();
        private readonly NumericUpDown decimals = new NumericUpDown();
        private readonly Label problems = new Label();

        internal SettingsForm(KzmSettings settings, bool requireComplete, string reason)
        {
            source = settings.Clone();
            this.requireComplete = requireComplete;
            Text = "КЗМ — настройки чертежа";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));

            if (!string.IsNullOrEmpty(reason))
            {
                var note = new Label { Text = reason, AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DarkBlue, Margin = new Padding(3, 3, 3, 9) };
                table.Controls.Add(note, 0, 0);
                table.SetColumnSpan(note, 2);
            }

            Section(table, "Импорт отметок");
            Row(table, "Шаблон числа (регулярное выражение)", pattern);
            Row(table, "Проверить шаблон на тексте", sample);
            sampleResult.AutoSize = true;
            sampleResult.MaximumSize = new Size(360, 0);
            Row(table, string.Empty, sampleResult);
            var range = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            min.Width = 110; max.Width = 110;
            range.Controls.AddRange(new Control[] { new Label { Text = "от", AutoSize = true, Margin = new Padding(0, 6, 3, 0) }, min,
                new Label { Text = "до", AutoSize = true, Margin = new Padding(9, 6, 3, 0) }, max });
            Row(table, "Диапазон отметок, м", range);
            Row(table, "Радиус поиска маркера R, м", radius);
            Row(table, "Допуск совпадения точек, м", tolerance);

            Section(table, "Оформление точек");
            scale.DropDownStyle = ComboBoxStyle.DropDownList;
            scale.Items.Add("— не задан —");
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            Row(table, "Масштаб", scale);
            Row(table, "Высота подписи отметки, мм на листе", labelHeight);
            Row(table, "Размер знака точки, мм на листе", pointSize);
            decimals.Minimum = 0; decimals.Maximum = 4; decimals.Width = 60;
            Row(table, "Знаков после запятой", decimals);

            problems.AutoSize = true;
            problems.MaximumSize = new Size(620, 0);
            problems.ForeColor = Color.Firebrick;
            problems.Margin = new Padding(3, 9, 3, 3);
            table.Controls.Add(problems);
            table.SetColumnSpan(problems, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Сохранить в чертеже", AutoSize = true };
            ok.Click += (s, e) => Accept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(table);

            pattern.Text = settings.NumberPattern;
            min.Text = Show(settings.MinElevation);
            max.Text = Show(settings.MaxElevation);
            radius.Text = Show(settings.MarkerRadius);
            tolerance.Text = Show(settings.DuplicateTolerance);
            scale.SelectedIndex = settings.ScaleDenominator.HasValue
                ? Math.Max(0, Array.IndexOf(KzmSettings.Scales, settings.ScaleDenominator.Value) + 1) : 0;
            labelHeight.Text = Show(settings.LabelHeightMm);
            pointSize.Text = Show(settings.PointSizeMm);
            decimals.Value = Math.Max(0, Math.Min(4, settings.Decimals));

            foreach (var box in new[] { pattern, sample, min, max }) box.TextChanged += (s, e) => TestSample();
            TestSample();
        }

        internal KzmSettings Result { get; private set; }

        private static void Section(TableLayoutPanel table, string title)
        {
            var label = new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 9, 3, 3) };
            table.Controls.Add(label);
            table.SetColumnSpan(label, 2);
        }

        private static void Row(TableLayoutPanel table, string title, Control control)
        {
            table.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 9, 3) });
            if (control is TextBox || control is ComboBox) control.Width = 340;
            table.Controls.Add(control);
        }

        private static string Show(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',') : string.Empty;

        private void TestSample()
        {
            if (string.IsNullOrWhiteSpace(sample.Text)) { sampleResult.Text = "Введите текст, например «т.15 145,32»."; return; }
            try
            {
                double low = ParseOptional(min.Text, "нижняя граница") ?? double.MinValue / 4;
                double high = ParseOptional(max.Text, "верхняя граница") ?? double.MaxValue / 4;
                var rules = new ElevationRules(pattern.Text, low, high, (int)decimals.Value);
                var result = ElevationExtractor.Extract(TextCleaner.CleanMText(sample.Text), rules);
                sampleResult.Text = result.Status == ImportStatus.Accepted
                    ? "Отметка: " + ElevationFormat.Format(result.Value.Value, (int)decimals.Value)
                    : (result.Status == ImportStatus.Disputed ? "Спорная: " : "Отброшено: ") + result.Message;
            }
            catch (KzmException exception)
            {
                sampleResult.Text = exception.Message;
            }
        }

        private void Accept()
        {
            var errors = new List<string>();
            var settings = source.Clone();
            settings.NumberPattern = pattern.Text.Trim();
            settings.MinElevation = Read(min.Text, "нижняя граница диапазона", errors);
            settings.MaxElevation = Read(max.Text, "верхняя граница диапазона", errors);
            settings.MarkerRadius = Read(radius.Text, "радиус поиска маркера", errors);
            double? tol = Read(tolerance.Text, "допуск совпадения", errors);
            if (!tol.HasValue) errors.Add("допуск совпадения точек не задан");
            else settings.DuplicateTolerance = tol.Value;
            settings.ScaleDenominator = scale.SelectedIndex > 0 ? KzmSettings.Scales[scale.SelectedIndex - 1] : (int?)null;
            settings.LabelHeightMm = Read(labelHeight.Text, "высота подписи", errors);
            settings.PointSizeMm = Read(pointSize.Text, "размер знака точки", errors);
            settings.Decimals = (int)decimals.Value;

            var all = settings.ValidateForImport();
            // Without the import flag only the values that are filled must be valid.
            errors.AddRange(requireComplete ? all : all.Where(problem => !problem.StartsWith("не задан", StringComparison.Ordinal)));
            if (errors.Count > 0)
            {
                problems.Text = "Исправьте: " + string.Join("; ", errors.Distinct()) + ".";
                return;
            }
            Result = settings;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static double? ParseOptional(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!ElevationFormat.TryParse(text, out double value)) throw new KzmException(name + ": введите число");
            return value;
        }

        private static double? Read(string text, string name, List<string> errors)
        {
            try { return ParseOptional(text, name); }
            catch (KzmException exception) { errors.Add(exception.Message); return null; }
        }
    }
}
