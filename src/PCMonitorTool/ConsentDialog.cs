using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCMonitorTool;

internal sealed class ConsentDialog : Window
{
    private readonly CheckBox _capture;
    internal bool CaptureEnabled => _capture.IsChecked == true;

    internal ConsentDialog(Window owner, MonitorSettings settings)
    {
        Title = "Начать сеанс · PC Monitor Tool";
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 530;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        void Add(string text, double size = 15)
        {
            panel.Children.Add(new TextBlock {
                Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12)
            });
        }
        Add("Что будет записываться", 22);
        Add($"Текущее приложение каждые {settings.SampleSeconds} с, запуск и завершение программ, подключение и отключение съёмных USB-дисков.");
        if (settings.UsbFileEvents)
            Add("Включён журнал изменений на USB. Он сохраняет имена файлов и доступный размер.");
        Add("Сеанс можно остановить в главном окне. Если его свернуть, кнопка остановки останется на компактной панели. При блокировке Windows учёт остановится.");
        _capture = new CheckBox {
            Content = new TextBlock { Text = $"Сохранять изображения всех экранов раз в {settings.ScreenshotMinutes} мин. В кадр могут попасть личные данные.", TextWrapping = TextWrapping.Wrap },
            // Согласие на периодические снимки даётся заново для каждого сеанса.
            IsChecked = false, Visibility = settings.ScreenshotsEnabled ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0,0,0,12)
        };
        panel.Children.Add(_capture);
        Add("Папка на этом компьютере: " + settings.DataDirectory, 13);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, IsDefault = true, Padding = new Thickness(14,6,14,6) };
        var start = new Button { Content = "Начать учёт", Padding = new Thickness(14,6,14,6), Margin = new Thickness(8,0,0,0) };
        start.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        panel.Children.Add(buttons);
    }
}
