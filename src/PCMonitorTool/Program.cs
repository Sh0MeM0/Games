using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PCMonitorTool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Единая система физических координат при разных масштабах мониторов.
            Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
            using var commands = new SessionCommands(args);
            if (!commands.IsPrimary)
            {
                if (commands.RequestShowAsync().GetAwaiter().GetResult()) return 0;
                MessageBox.Show("Панель уже запущена, но пока не отвечает. Откройте её из области уведомлений.", "PC Monitor Tool");
                return 1;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = MonitorSettings.Load();
            using var controller = new Controller(app, commands, settings);
            app.Startup += (_, _) => controller.Open();
            return app.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить PC Monitor Tool.\n\n" + ex.Message,
                "PC Monitor Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

internal sealed class Controller : IDisposable
{
    private readonly Application _app;
    private readonly SessionCommands _commands;
    private readonly MainWindow _window = new();
    private readonly Forms.NotifyIcon _icon = new();
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly CancellationTokenSource _exportCancel = new();
    private HotkeyManager? _hotkey;
    private Forms.ToolStripItem? _stopItem;
    private MonitorSettings _settings;
    private DataStore _data;
    private MonitorService? _monitor;
    private ScreenshotService? _screen;
    private bool _automatic, _dialogOpen, _exiting;
    private Task? _stopping, _exporting;
    private string _detail = "Сбор начнётся после подтверждения.";
    private long _generation;

    internal Controller(Application app, SessionCommands commands, MonitorSettings settings)
    {
        _app = app;
        _commands = commands;
        _settings = settings;
        _data = new DataStore(settings.DataDirectory);
        _app.MainWindow = _window;
    }

    internal void Open()
    {
        _window.SetStore(_data);
        _window.ExitRequested += () => Run(ExitAsync);
        _window.StartRequested += () => Run(StartAsync);
        _window.StopRequested += () => Run(() => StopAsync("Сеанс остановлен пользователем."));
        _window.CaptureRequested += () => Run(CaptureAsync);
        _window.SettingsRequested += () => Run(ShowSettingsAsync);
        _window.ExportRequested += () => Run(ExportAsync);
        _window.OpenFolderRequested += () => Run(() => {
            Process.Start(new ProcessStartInfo(_data.Root) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        _menu.Items.Add("Открыть панель", null, (_, _) => Show());
        _stopItem = _menu.Items.Add("Остановить учёт", null, (_, _) => Run(() => StopAsync("Сеанс остановлен пользователем.")));
        _menu.Items.Add("Выход", null, (_, _) => Run(ExitAsync));
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => Show();
        Update();
        _icon.Visible = true;
        _window.Closing += OnClosing;
        _window.Show();
        _hotkey = new HotkeyManager(_window, Show);
        if (!_hotkey.Register(_settings.HotkeyFunctionKey))
            SetDetail("Сочетание клавиш занято. Выберите другое в настройках.");
        _commands.Listen(Show);
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _app.SessionEnding += OnSessionEnding;

        // Уведомление показывается один раз. Согласие на сбор запрашивается при каждом старте сеанса.
        const string key = @"HKEY_CURRENT_USER\Software\PCMonitorTool";
        if (Registry.GetValue(key, "NoticeVersion", 0) as int? != 1)
        {
            MessageBox.Show(_window,
                "На этом ПК установлена программа учёта времени. Перед началом каждого сеанса вы увидите перечень собираемых данных. Вы можете отказаться, изменить настройки или остановить учёт через значок рядом с часами.\n\nЖурналы и снимки сохраняются в выбранной папке на этом компьютере.",
                "PC Monitor Tool · Уведомление", MessageBoxButton.OK, MessageBoxImage.Information);
            Registry.SetValue(key, "NoticeVersion", 1, RegistryValueKind.DWord);
        }
    }

    // Все изменения сеанса и интерфейса выполняются в одном диспетчере WPF.
    private void Show()
    {
        if (!_exiting) _window.ShowDashboard();
    }

    private void Update()
    {
        bool active = _monitor is not null;
        _window.SetStatus(active, _automatic, _detail);
        _icon.Icon = active ? System.Drawing.SystemIcons.Information : System.Drawing.SystemIcons.Application;
        _icon.Text = active ? "PC Monitor Tool · Идёт учёт" : "PC Monitor Tool · Учёт остановлен";
        if (_stopItem != null) _stopItem.Enabled = active;
    }

    private void SetDetail(string detail)
    {
        _detail = detail;
        if (!_exiting) Update();
    }

    private async void Run(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_exiting)
            {
                SetDetail(ex.Message);
                MessageBox.Show(_window, ex.Message, "PC Monitor Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async Task StartAsync()
    {
        if (_monitor != null || _stopping != null || _exiting || _dialogOpen) return;
        Show();
        long version = _generation;
        var consent = new ConsentDialog(_window, _settings);
        _dialogOpen = true;
        bool accepted;
        try { accepted = consent.ShowDialog() == true; }
        finally { _dialogOpen = false; }
        if (!accepted || _exiting || version != _generation) return;
        long current = ++_generation;
        void Report(string message) => _app.Dispatcher.BeginInvoke(new Action(() => {
            if (_generation == current && !_exiting) SetDetail(message);
        }));
        void UnexpectedStop() => _app.Dispatcher.BeginInvoke(new Action(() => {
            if (_generation == current && !_exiting) Run(() => StopAsync(_detail));
        }));
        _monitor = new MonitorService(_data, _settings, Report, UnexpectedStop);
        _screen = new ScreenshotService(_data, Report, () => Run(() => StopAsync("Сеанс остановлен с панели снимков.")));
        try
        {
            _automatic = consent.CaptureEnabled;
            if (_automatic) _screen.StartAutomatic(_settings.ScreenshotMinutes);
            _monitor.Start();
            SetDetail("Учёт начат " + DateTime.Now.ToString("HH:mm") + ". Закрытие панели сворачивает её в область уведомлений.");
        }
        catch
        {
            await StopAsync("Не удалось начать сеанс.");
            throw;
        }
    }

    private Task StopAsync(string detail)
    {
        // Запретить запуск из диалога согласия, если компьютер успел заблокироваться.
        ++_generation;
        if (_stopping != null) return _stopping;
        if (_monitor == null && _screen == null) return Task.CompletedTask;
        var monitor = _monitor;
        var screen = _screen;
        _monitor = null;
        _screen = null;
        _automatic = false;
        var pending = Task.WhenAll(screen?.StopAsync() ?? Task.CompletedTask, monitor?.StopAsync() ?? Task.CompletedTask);
        SetDetail(detail);
        _stopping = pending;
        return FinishStopAsync(pending);
    }

    private async Task FinishStopAsync(Task pending)
    {
        try { await pending; }
        finally
        {
            _stopping = null;
            if (!_exiting) _window.RefreshView();
        }
    }

    private async Task CaptureAsync()
    {
        if (_exiting || _dialogOpen) return;
        var screen = _screen;
        if (screen == null)
        {
            SetDetail("Для снимка начните сеанс учёта.");
            return;
        }
        if (MessageBox.Show(_window, "Сохранить содержимое всех подключённых экранов в PNG?",
            "Снимок сейчас", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (screen != _screen || _exiting) return;
        await screen.CaptureNowAsync();
        if (screen == _screen) SetDetail("Снимки сохранены. Они доступны на вкладке «Скриншоты».");
    }

    private Task ShowSettingsAsync()
    {
        if (_dialogOpen || _exiting) return Task.CompletedTask;
        if (_monitor != null || _stopping != null || _exporting != null)
        {
            MessageBox.Show(_window, "Настройки можно изменить после остановки сеанса и завершения экспорта.", "Настройки");
            return Task.CompletedTask;
        }
        _dialogOpen = true;
        try { new SettingsForm(_settings, ApplySettings) { Owner = _window }.ShowDialog(); }
        finally { _dialogOpen = false; }
        return Task.CompletedTask;
    }

    private void ApplySettings(MonitorSettings next, bool runCommand)
    {
        bool oldCommand = SessionCommands.IsRunCommandRegistered();
        DataStore? candidate = null;
        try
        {
            if (!string.Equals(_data.Root, next.DataDirectory, StringComparison.OrdinalIgnoreCase))
                candidate = new DataStore(next.DataDirectory);
            if (_hotkey?.Register(next.HotkeyFunctionKey) != true)
                throw new InvalidOperationException("Горячая клавиша недоступна. Выберите другое сочетание.");
            try
            {
                if (next.AutoStart != _settings.AutoStart) SessionCommands.ConfigureRegistration(next.AutoStart);
                if (runCommand != oldCommand) SessionCommands.ConfigureRunCommand(runCommand);
                next.Save();
            }
            catch
            {
                _hotkey.Register(_settings.HotkeyFunctionKey);
                if (next.AutoStart != _settings.AutoStart) SessionCommands.ConfigureRegistration(_settings.AutoStart);
                if (runCommand != oldCommand) SessionCommands.ConfigureRunCommand(oldCommand);
                throw;
            }
            _settings = next;
            if (candidate is not null)
            {
                var old = _data;
                _data = candidate;
                candidate = null;
                _window.SetStore(_data);
                old.Dispose();
            }
            SetDetail("Настройки сохранены.");
        }
        finally { candidate?.Dispose(); }
    }

    private async Task ExportAsync()
    {
        if (_exporting != null || _dialogOpen || _exiting) return;
        var dialog = new SaveFileDialog {
            Title = "Экспорт локальных данных", DefaultExt = ".zip", AddExtension = true,
            Filter = "Архив ZIP (*.zip)|*.zip", FileName = $"PCMonitor_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(_window) != true || _exiting) return;
        SetDetail("Создаётся ZIP-архив…");
        // Сжатие и обход папки могут занять время даже при быстром диске.
        string destination = dialog.FileName;
        _exporting = Task.Run(() => _data.ExportAsync(destination, _exportCancel.Token));
        try { await _exporting; SetDetail("Экспорт завершён: " + destination); }
        finally { _exporting = null; }
    }

    private void SystemStop(string reason)
    {
        if (!_app.Dispatcher.HasShutdownStarted)
            _app.Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) Run(() => StopAsync(reason)); }));
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff)
            SystemStop("Сеанс остановлен при блокировке или отключении Windows.");
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SystemStop("Сеанс остановлен при переходе в спящий режим.");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        _window.Hide();
        _icon.ShowBalloonTip(2500, "PC Monitor Tool", "Панель доступна через значок рядом с часами.", Forms.ToolTipIcon.Info);
    }
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _exportCancel.Cancel();
        try { await StopAsync("Завершение работы."); }
        finally
        {
            try { if (_exporting != null) await _exporting; }
            catch (OperationCanceledException) { }
            finally { _app.Shutdown(); }
        }
    }
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        _exportCancel.Cancel();
        // Windows ограничивает время выхода. Не задерживаем завершение сеанса модальным окном.
        _ = StopAsync("Выход из Windows.");
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _app.SessionEnding -= OnSessionEnding;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _hotkey?.Dispose();
        _data.Dispose();
        _exportCancel.Dispose();
    }
}
