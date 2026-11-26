using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PCMonitorTool;

public partial class SettingsForm : Window
{
    private readonly MonitorSettings _settings;
    private readonly Action<MonitorSettings, bool> _apply;

    public SettingsForm(MonitorSettings settings, Action<MonitorSettings, bool> apply)
    {
        InitializeComponent();
        _settings = settings;
        _apply = apply;
        Folder.Text = settings.DataDirectory;
        Sample.Text = settings.SampleSeconds.ToString();
        Minutes.Text = settings.ScreenshotMinutes.ToString();
        Shots.IsChecked = settings.ScreenshotsEnabled;
        Usb.IsChecked = settings.UsbFileEvents;
        Auto.IsChecked = settings.AutoStart;
        RunCommand.IsChecked = SessionCommands.IsRunCommandRegistered();
        for (var n = 1; n <= 12; n++) KeyBox.Items.Add($"F{n}");
        KeyBox.SelectedIndex = settings.HotkeyFunctionKey - 1;
    }

    private void Browse_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для локальных журналов и снимков" };
        if (dialog.ShowDialog(this) == true) Folder.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (!int.TryParse(Sample.Text, out int seconds) || seconds is < 5 or > 300 ||
            !int.TryParse(Minutes.Text, out int minutes) || minutes is < 1 or > 240)
        {
            ErrorLabel.Text = "Введите целые числа в пределах указанных интервалов.";
            return;
        }
        try
        {
            string path = Environment.ExpandEnvironmentVariables(Folder.Text.Trim());
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Укажите полный путь к папке.");
            var next = _settings with {
                DataDirectory = Path.GetFullPath(path), SampleSeconds = seconds, ScreenshotMinutes = minutes,
                ScreenshotsEnabled = Shots.IsChecked == true, UsbFileEvents = Usb.IsChecked == true,
                AutoStart = Auto.IsChecked == true, HotkeyFunctionKey = KeyBox.SelectedIndex + 1
            };
            _apply(next, RunCommand.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; }
    }
}
