using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Общие настройки чертежа. Оформление точек задаётся в поверхности (берётся с образца),
    /// оформление картограммы — в картограмме.
    /// </summary>
    internal sealed class SettingsView : UserControl, IPanelView
    {
        private readonly NumericUpDown decimals = new NoWheelNumericUpDown { Minimum = 0, Maximum = 4, Width = 60 };
        private readonly ComboBox scale = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        private readonly Label problems = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.Firebrick };
        private readonly Label templateNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0) };
        /// <summary>Шрифт текстов картограммы (замечание 27); первая строка — текущий стиль чертежа.</summary>
        private readonly ComboBox font = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, MaxDropDownItems = 20 };
        private readonly CheckBox italic = new CheckBox { Text = "наклонный", AutoSize = true };
        private readonly Label fontNote = new Label { AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = SystemColors.GrayText };
        private const string CurrentStyle = "(текущий стиль чертежа)";
        private KzmPanel panel;
        private KzmSettings loaded;

        internal SettingsView()
        {
            AutoScroll = true;
            var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var header = new Label { Text = "Настройки чертежа", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 9, 3, 3) };
            table.Controls.Add(header);
            table.SetColumnSpan(header, 2);
            foreach (int denominator in KzmSettings.Scales) scale.Items.Add("1:" + denominator.ToString(CultureInfo.InvariantCulture));
            Row(table, "Знаков после запятой в отметках", decimals);
            Row(table, "Масштаб новых картограмм", scale);
            var fontRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            fontRow.Controls.Add(font);
            fontRow.Controls.Add(italic);
            Row(table, "Шрифт текстов картограммы", fontRow);
            table.Controls.Add(fontNote);
            table.SetColumnSpan(fontNote, 2);
            table.Controls.Add(problems);
            table.SetColumnSpan(problems, 2);
            var save = new Button { Text = "Сохранить в чертеже", AutoSize = true };
            save.Click += (s, e) => Save();
            table.Controls.Add(save);
            table.SetColumnSpan(save, 2);
            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(330, 0),
                Text = "Стиль, высота и цвет подписей точек — в свойствах поверхности (берутся с текста-образца). " +
                       "Шаг, масштаб и высоты текста картограммы — в свойствах картограммы (по умолчанию 0,9 в чертеже: " +
                       "0,9 мм на листе при 1:1000, 1,8 мм при 1:500).",
            };
            table.Controls.Add(note);
            table.SetColumnSpan(note, 2);

            var templateHeader = new Label { Text = "Шаблон по умолчанию", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 14, 3, 3) };
            table.Controls.Add(templateHeader);
            table.SetColumnSpan(templateHeader, 2);
            table.Controls.Add(templateNote);
            table.SetColumnSpan(templateNote, 2);
            var saveTemplate = new Button { Text = "Сохранить как шаблон", AutoSize = true };
            saveTemplate.Click += (s, e) => SaveTemplate();
            var resetTemplate = new Button { Text = "Удалить шаблон", AutoSize = true };
            resetTemplate.Click += (s, e) => ResetTemplate();
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            buttons.Controls.Add(saveTemplate);
            buttons.Controls.Add(resetTemplate);
            table.Controls.Add(buttons);
            table.SetColumnSpan(buttons, 2);
            Controls.Add(table);
        }

        private static void Row(TableLayoutPanel table, string caption, Control control)
        {
            table.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(170, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) });
            table.Controls.Add(control);
        }

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            loaded = panel.Snapshot.Settings.Clone();
            decimals.Value = Math.Max(0, Math.Min(4, loaded.Decimals));
            scale.SelectedIndex = Math.Max(0, Array.IndexOf(KzmSettings.Scales, loaded.Scale));
            ShowFont(loaded.Font, loaded.FontItalic);
            problems.Text = string.Empty;
            ShowTemplate();
        }

        /// <summary>The list of fonts is filled once; a chosen font that is not installed stays in the list, marked.</summary>
        private void ShowFont(string chosen, bool isItalic)
        {
            if (font.Items.Count == 0)
            {
                font.BeginUpdate();
                font.Items.Add(CurrentStyle);
                foreach (var name in Drawing.CartogramText.InstalledFonts()) font.Items.Add(name);
                font.EndUpdate();
            }
            chosen = chosen?.Trim();
            if (string.IsNullOrEmpty(chosen)) font.SelectedIndex = 0;
            else
            {
                int index = font.FindStringExact(chosen);
                if (index < 0) index = font.Items.Add(chosen);
                font.SelectedIndex = index;
            }
            italic.Checked = isItalic;
            bool missing = !string.IsNullOrEmpty(chosen) && !Drawing.CartogramText.IsInstalled(chosen);
            fontNote.ForeColor = missing ? Color.Firebrick : SystemColors.GrayText;
            fontNote.Text = missing
                ? "Шрифт «" + chosen + "» не установлен в Windows: пока его нет, тексты картограммы пишутся текущим стилем чертежа."
                : "Отметки, объёмы, таблицы, площади и ведомость пишутся стилем «" + Drawing.CartogramText.StyleName + "» с этим шрифтом. " +
                  "Подписи точек поверхностей — своим стилем (в свойствах поверхности).";
        }

        private string ChosenFont() => font.SelectedIndex <= 0 ? string.Empty : font.SelectedItem as string;

        private void ShowTemplate()
        {
            var template = Drawing.KzmDefaults.Load(out string problem);
            if (problem != null) { templateNote.Text = problem; templateNote.ForeColor = Color.Firebrick; return; }
            templateNote.ForeColor = SystemColors.ControlText;
            if (!template.HasValue)
            {
                templateNote.Text = "Шаблона нет: новые чертежи и картограммы получают значения модуля (2 знака, 1:1000, шаг 10 м, текст 0,9 в чертеже, подписи по примеру Б.4). " +
                                    "«Сохранить как шаблон» запомнит эти настройки для новых чертежей; параметры картограммы — кнопкой «Сделать шаблоном» в картограмме.";
                return;
            }
            var (settings, c) = template.Value;
            templateNote.Text = "Файл " + Drawing.KzmDefaults.FilePath + ":\nотметки — знаков " + settings.Decimals + ", масштаб 1:" + settings.Scale +
                                "; картограмма — шаг " + FormatValue(c.Step) + " м, масштаб 1:" + c.Scale + ", текст " + FormatValue(c.NodeTextMm) + "/" +
                                FormatValue(c.VolumeTextMm) + "/" + FormatValue(c.TotalsTextMm) + " мм, рабочая отметка " + NodeLabelStyle.Title(c.WorkingLabel.Corner) + ".";
        }

        private void SaveTemplate()
        {
            var settings = loaded.Clone();
            settings.Decimals = (int)decimals.Value;
            settings.Scale = KzmSettings.Scales[Math.Max(0, scale.SelectedIndex)];
            settings.Font = ChosenFont();
            settings.FontItalic = italic.Checked;
            try
            {
                var template = Drawing.KzmDefaults.Load()?.Template ?? new CartogramInfo();
                Drawing.KzmDefaults.Save(settings, template);
                PanelMessages.Info("Шаблон по умолчанию сохранён: " + Drawing.KzmDefaults.FilePath + ".");
            }
            catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException)
            {
                PanelMessages.Error("шаблон не записан: " + exception.Message);
            }
            ShowTemplate();
        }

        private void ResetTemplate()
        {
            try
            {
                Drawing.KzmDefaults.Reset();
                PanelMessages.Info("Шаблон по умолчанию удалён: дальше — значения модуля.");
            }
            catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException)
            {
                PanelMessages.Error("шаблон не удалён: " + exception.Message);
            }
            ShowTemplate();
        }

        internal static string FormatValue(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',') : string.Empty;

        internal static double? Parse(string text, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (ElevationFormat.TryParse(text, out double value)) return value;
            errors.Add(name + ": введите число");
            return null;
        }

        private void Save()
        {
            if (panel?.Document == null) return;
            var settings = loaded.Clone();
            settings.Decimals = (int)decimals.Value;
            settings.Scale = KzmSettings.Scales[Math.Max(0, scale.SelectedIndex)];
            settings.Font = ChosenFont();
            settings.FontItalic = italic.Checked;
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                problems.Text = "Исправьте: " + string.Join("; ", errors) + ".";
                return;
            }
            var previous = loaded;
            PanelActions.Run("настройки", document =>
            {
                int changed = EditServices.SaveSettings(document.Database, previous, settings);
                // The texts of built cartograms follow their style: a new font shows at once.
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Drawing.CartogramText.Update(document.Database, transaction, settings);
                    transaction.Commit();
                }
                document.Editor.Regen();
                PanelMessages.Info("Настройки сохранены." + (changed > 0 ? " Подписи точек переписаны: " + changed + "." : string.Empty));
            }, () =>
            {
                panel.SettingsSaved();
                panel.RefreshAll();
            });
        }
    }
}
