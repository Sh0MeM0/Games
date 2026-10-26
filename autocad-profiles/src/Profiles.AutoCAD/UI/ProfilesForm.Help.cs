using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private const string QuickHelp =
            "ПОРЯДОК РАБОТЫ\r\n" +
            "1. Вверху выберите проект и тип профиля: «Подчистка канала», «Новый канал» или «Сети НВК». " +
            "Другой тип создаёт новую пустую вкладку; прежние данные остаются на своей вкладке.\r\n" +
            "2. Вкладка «Продольный профиль»: введите пикеты и отметки. ПК 6+50 = 650 м, число 6 = 6 м. " +
            "Пустая ячейка не равна нулю; курсив — значение, интерполированное между соседними пикетами.\r\n" +
            "3. Вкладка «Поперечные профили»: слева выберите или добавьте поперечник и задайте ему ПК. " +
            "Каждая колонка таблицы — точка слева направо по течению; в окне помещается 13 точек. Кнопка «Роль ▾» / «Назначить ▾» в шапке задаёт берег или дно.\r\n" +
            "   Назначенные берега обозначаются на чертеже «Л.Б.» и «П.Б.» с линиями к точкам. Номера и расстояния — только для существующих отметок; проектные точки имеют отметки и пунктирные выносы.\r\n" +
            "   Чтобы перенести промер в продольник, введите 0+20 или 20 в «Имя / ПК» и нажмите Enter. " +
            "Кнопка «Назначить на продольный…» позволяет выбрать готовый пикет. Проверьте берега и дно, затем перестройте продольник.\r\n" +
            "4. Нажмите «Проверить и показать» (F5): программа проверит данные и покажет предпросмотр.\r\n" +
            "5. «Построить текущий» — указать точку вставки в чертеже; «Перестроить текущий» — обновить прежнее построение на месте.\r\n" +
            "6. Сохраните сам чертёж AutoCAD (Ctrl+S после закрытия окна): данные проектов хранятся внутри DWG.\r\n\r\n" +
            "ГОРЯЧИЕ КЛАВИШИ\r\n" +
            "F1 — эта справка;  F5 — проверить и показать;  Ctrl+S — сохранить проект в DWG;  Ctrl+O — импорт JSON;\r\n" +
            "Ctrl+V — вставить таблицу из Excel (сначала выберите первую ячейку);  Del — очистить выбранные ячейки поперечника;  Esc — закрыть окно.\r\n\r\n" +
            "ПОЛЕЗНО ЗНАТЬ\r\n" +
            "• Все расстояния и отметки — в метрах; дробная часть — через точку или запятую, без разделителей тысяч.\r\n" +
            "• Откос 1:1,5 вводится как 1,5 (горизонталь к вертикали).\r\n" +
            "• Между известными пикетами интерполируются отметки, ширина дна и оба откоса. Курсивные значения пересчитываются при изменении соседей.\r\n" +
            "• «Взять из чертежа…» набирает пикеты и отметки щелчками по плану; Enter пропускает отметку, Esc отменяет набор.\r\n" +
            "• «Загрузить пример» создаёт отдельный проект; лишний проект удаляется кнопкой «Удалить проект…».\r\n" +
            "• Если верхнее меню «Профиля» пропало, выполните в AutoCAD команду PROFILI_MENU.\r\n" +
            "• Модуль не выполняет гидравлических расчётов и проверки норм — проверьте результат перед выпуском чертежа.";

        private void ShowHelp()
        {
            using (var dialog = new Form
            {
                Text = "Профиля — справка", Font = Font, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, ShowInTaskbar = false, ClientSize = new Size(Px(760), Px(520)),
                MinimumSize = new Size(Px(480), Px(320))
            })
            {
                var text = new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    BackColor = Color.White, BorderStyle = BorderStyle.None, Text = QuickHelp,
                    AccessibleName = "Текст справки", Margin = new Padding(0)
                };
                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false, Padding = new Padding(Px(6))
                };
                var close = new Button { Text = "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Px(90), 0) };
                var guide = new Button { Text = "Полное руководство…", AutoSize = true };
                guide.Click += (s, e) => OpenGuideFolder(dialog);
                buttons.Controls.Add(close);
                buttons.Controls.Add(guide);
                var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(12)), BackColor = Color.White };
                page.Controls.Add(text);
                dialog.Controls.Add(page);
                dialog.Controls.Add(buttons);
                dialog.CancelButton = close;
                dialog.AcceptButton = close;
                tips.SetToolTip(guide, "Открыть папку установленного модуля с файлом README.md (подробная инструкция).");
                dialog.Shown += (s, e) => { text.SelectionLength = 0; close.Focus(); };
                dialog.ShowDialog(this);
            }
        }

        private static void OpenGuideFolder(IWin32Window owner)
        {
            try
            {
                // .../Sh0MeM0.Profiles.bundle/Contents/<year>/Profiles.AutoCAD.dll
                string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var bundle = Directory.GetParent(assemblyFolder)?.Parent;
                if (bundle == null || !bundle.Exists) throw new DirectoryNotFoundException("Папка модуля не найдена.");
                string readme = Path.Combine(bundle.FullName, "README.md");
                // Explorer only receives a quoted path inside the installed bundle.
                string arguments = File.Exists(readme) ? "/select,\"" + readme + "\"" : "\"" + bundle.FullName + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(owner, "Не удалось открыть папку модуля: " + error.Message +
                    "\n\nРуководство README.md находится в папке установки: C:\\Program Files\\Autodesk\\ApplicationPlugins\\Sh0MeM0.Profiles.bundle",
                    "Профиля", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
