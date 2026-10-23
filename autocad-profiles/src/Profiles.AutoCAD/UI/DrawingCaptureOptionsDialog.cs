using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD.UI
{
    internal sealed class DrawingCaptureOptionsDialog : Form
    {
        private sealed class ModeChoice
        {
            internal DrawingCaptureSourceMode Mode;
            internal string Caption;
            public override string ToString() => Caption;
        }

        private sealed class NumberInput : TextBox
        {
            private readonly double original;
            private bool edited;

            internal NumberInput(double value)
            {
                original = value;
                Dock = DockStyle.Fill;
                MaxLength = 512;
                Text = DecimalText(value);
                TextChanged += (sender, args) => edited = true;
            }

            internal double Read(string name)
            {
                double value = edited ? NumberParser.ParseRequired(Text, name) : original;
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new ProfileValidationException(name + ": введите конечное число.");
                return value;
            }
        }

        private readonly ProfileKind kind;
        private readonly DrawingCaptureOptions initial;
        private readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly NumberInput paperScale;
        private readonly NumberInput metresPerUnit;
        private readonly NumberInput startStation;
        private readonly Label paperCaption = Caption("Бумажный масштаб, 1 :");
        private readonly Label factorCaption = Caption("Метров в единице чертежа:");
        private readonly Label conversion = Note("");
        private readonly Label error = Note("");
        private DrawingCaptureOptions result;

        internal static DrawingCaptureOptions ShowOptions(IWin32Window owner, ProfileKind kind, DrawingCaptureOptions initial)
        {
            using (var dialog = new DrawingCaptureOptionsDialog(kind, initial))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.result?.Clone() : null;
        }

        private DrawingCaptureOptionsDialog(ProfileKind kind, DrawingCaptureOptions initial)
        {
            this.kind = kind;
            this.initial = (initial ?? new DrawingCaptureOptions()).Clone();
            paperScale = new NumberInput(this.initial.PaperScale);
            metresPerUnit = new NumberInput(this.initial.MetresPerDrawingUnit);
            startStation = new NumberInput(this.initial.StartStation);

            Text = "Съёмка из чертежа — исходные единицы";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(500, 370);

            mode.Items.Add(new ModeChoice { Mode = DrawingCaptureSourceMode.ModelMetres, Caption = "Модель: 1 единица чертежа = 1 метр" });
            mode.Items.Add(new ModeChoice { Mode = DrawingCaptureSourceMode.PaperMillimetres, Caption = "Миллиметры на бумаге в масштабе 1 : N" });
            mode.Items.Add(new ModeChoice { Mode = DrawingCaptureSourceMode.Custom, Caption = "Задать метров в единице чертежа" });
            mode.Items.Add(new ModeChoice { Mode = DrawingCaptureSourceMode.Calibration, Caption = "Калибровать по известному расстоянию" });

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            for (int row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(row == 7 ? SizeType.Percent : SizeType.AutoSize, row == 7 ? 100 : 0));
            AddWide(layout, Note("Это единицы исходной съёмки. Они не меняют масштаб построения профиля."), 0);
            AddWide(layout, mode, 1);
            layout.Controls.Add(paperCaption, 0, 2);
            layout.Controls.Add(paperScale, 1, 2);
            layout.Controls.Add(factorCaption, 0, 3);
            layout.Controls.Add(metresPerUnit, 1, 3);
            AddWide(layout, conversion, 4);

            var stationCaption = Caption("Начальный ПК (например 1+25):");
            layout.Controls.Add(stationCaption, 0, 5);
            layout.Controls.Add(startStation, 1, 5);
            stationCaption.Visible = startStation.Visible = kind == ProfileKind.Longitudinal;
            AddWide(layout, Note("Далее укажите точки съёмки и отметки. Enter пропускает отметку или завершает набор; Esc отменяет текущий набор. Роли точек можно изменить в таблице."), 6);
            error.ForeColor = Color.Firebrick;
            AddWide(layout, error, 7);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var accept = new Button { Text = "Продолжить", AutoSize = true };
            accept.Click += (sender, args) => AcceptOptions();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(accept);
            AddWide(layout, buttons, 8);
            Controls.Add(layout);
            AcceptButton = accept;
            CancelButton = cancel;

            mode.SelectedIndexChanged += (sender, args) => UpdateMode();
            paperScale.TextChanged += (sender, args) => UpdateConversion();
            metresPerUnit.TextChanged += (sender, args) => UpdateConversion();
            mode.SelectedIndex = 0;
            for (int index = 0; index < mode.Items.Count; index++)
                if (((ModeChoice)mode.Items[index]).Mode == this.initial.SourceMode) mode.SelectedIndex = index;
        }

        private DrawingCaptureSourceMode SelectedMode => ((ModeChoice)mode.SelectedItem).Mode;

        private void UpdateMode()
        {
            if (mode.SelectedItem == null) return;
            paperCaption.Visible = paperScale.Visible = SelectedMode == DrawingCaptureSourceMode.PaperMillimetres;
            factorCaption.Visible = metresPerUnit.Visible = SelectedMode == DrawingCaptureSourceMode.Custom;
            error.Text = "";
            UpdateConversion();
        }

        private void UpdateConversion()
        {
            if (mode.SelectedItem == null) return;
            if (SelectedMode == DrawingCaptureSourceMode.Calibration)
            {
                conversion.Text = "В AutoCAD укажите две точки и реальное расстояние между ними в метрах. Коэффициент определится по этим данным.";
                return;
            }
            try
            {
                double factor = SelectedMode == DrawingCaptureSourceMode.ModelMetres ? 1 :
                    SelectedMode == DrawingCaptureSourceMode.PaperMillimetres ? Positive(paperScale.Read("Бумажный масштаб"), "Бумажный масштаб") / 1000 :
                    Positive(metresPerUnit.Read("Метры в единице чертежа"), "Метры в единице чертежа");
                conversion.Text = "1 единица исходного чертежа = " + DecimalText(Positive(factor, "Коэффициент пересчёта")) + " м.";
            }
            catch (ProfileValidationException) { conversion.Text = "Введите положительное число с точкой или запятой."; }
        }

        private void AcceptOptions()
        {
            try
            {
                var options = initial.Clone();
                options.SourceMode = SelectedMode;
                if (SelectedMode == DrawingCaptureSourceMode.PaperMillimetres)
                {
                    options.PaperScale = Positive(paperScale.Read("Бумажный масштаб"), "Бумажный масштаб");
                    options.MetresPerDrawingUnit = options.PaperScale / 1000;
                }
                else if (SelectedMode == DrawingCaptureSourceMode.Custom)
                    options.MetresPerDrawingUnit = Positive(metresPerUnit.Read("Метры в единице чертежа"), "Метры в единице чертежа");
                else if (SelectedMode == DrawingCaptureSourceMode.ModelMetres) options.MetresPerDrawingUnit = 1;

                Positive(options.PaperScale, "Бумажный масштаб");
                Positive(options.MetresPerDrawingUnit, "Метры в единице чертежа");
                options.StartStation = kind == ProfileKind.Longitudinal ? startStation.Read("Начальный ПК") : initial.StartStation;
                if (double.IsNaN(options.StartStation) || double.IsInfinity(options.StartStation))
                    throw new ProfileValidationException("Начальный ПК должен быть конечным числом.");
                result = options;
                DialogResult = DialogResult.OK;
            }
            catch (ProfileValidationException ex) { error.Text = ex.Message; }
        }

        private static double Positive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ProfileValidationException(name + ": введите конечное положительное число.");
            return value;
        }

        private static string DecimalText(double value)
        {
            // Expand round-trip scientific notation so the text remains editable by
            // NumberParser, while an untouched control always returns the raw double.
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            int exponentAt = text.IndexOfAny(new[] { 'E', 'e' });
            if (exponentAt < 0) return text;
            string mantissa = text.Substring(0, exponentAt);
            bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
            if (negative) mantissa = mantissa.Substring(1);
            int decimalAt = mantissa.IndexOf('.');
            if (decimalAt < 0) decimalAt = mantissa.Length;
            string digits = mantissa.Replace(".", "");
            int position = decimalAt + int.Parse(text.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            string expanded = position <= 0 ? "0." + new string('0', -position) + digits :
                position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, ".");
            return (negative ? "-" : "") + expanded;
        }

        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 8, 6) };
        private static Label Note(string text) => new Label { Text = text, AutoSize = true, MaximumSize = new Size(465, 0), Margin = new Padding(0, 4, 0, 8) };
        private static void AddWide(TableLayoutPanel layout, Control control, int row)
        { layout.Controls.Add(control, 0, row); layout.SetColumnSpan(control, 2); }
    }
}
