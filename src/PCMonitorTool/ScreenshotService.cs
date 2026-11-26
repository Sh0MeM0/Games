using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;

namespace PCMonitorTool;

/// <summary>Снимки открытого рабочего стола текущего интерактивного сеанса.</summary>
public sealed class ScreenshotService
{
    private readonly DataStore _store;
    private readonly Action<string> _reportError;
    private readonly Action _stopSession;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _cancel = new();
    private Task _pending = Task.CompletedTask;
    private Task? _shutdown;
    private DispatcherTimer? _timer;
    private CaptureIndicator? _indicator;
    private int? _captureMinutes;
    private bool _dashboardMinimized;
    private Action? _showDashboard;
    private bool _stopping;

    public ScreenshotService(DataStore store, Action<string> reportError, Action stopSession)
    {
        _store = store;
        _reportError = reportError;
        _stopSession = stopSession;
    }

    // Вызывается кнопкой в окне управления. Параллельные запросы объединяются.
    public Task CaptureNowAsync()
    {
        _ui.VerifyAccess();
        if (_stopping)
            throw new InvalidOperationException("Сеанс завершён.");

        return BeginCapture(_captureMinutes.HasValue ? _indicator?.Handle ?? IntPtr.Zero : null);
    }

    // Вызывается только после подтверждения на текущий сеанс. Панель открывается до таймера.
    public void StartAutomatic(int intervalMinutes)
    {
        _ui.VerifyAccess();
        if (_stopping || _timer is not null)
            throw new InvalidOperationException("Сеанс уже завершён или таймер включён.");
        if (intervalMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));

        _captureMinutes = intervalMinutes;
        UpdateIndicator();
        _timer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromMinutes(intervalMinutes)
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void SetDashboardMinimized(bool minimized, Action showDashboard)
    {
        _ui.VerifyAccess();
        if (_stopping) return;
        _dashboardMinimized = minimized;
        _showDashboard = showDashboard;
        UpdateIndicator();
    }

    private void UpdateIndicator()
    {
        // Единственное окно служит панелью сеанса и индикатором автоматических снимков.
        if (!_captureMinutes.HasValue && !_dashboardMinimized)
        {
            CloseIndicator();
            return;
        }
        bool newlyCreated = _indicator is null;
        _indicator ??= new CaptureIndicator(RequestStop);
        _indicator.SetMode(_captureMinutes, _dashboardMinimized ? _showDashboard : null);
        if (newlyCreated) _indicator.Show();
    }

    private void CloseIndicator()
    {
        var indicator = _indicator;
        _indicator = null;
        indicator?.CloseProgrammatically();
    }

    private void RequestStop()
    {
        if (_stopping) return;
        _stopping = true;
        _timer?.Stop();
        _cancel.Cancel();
        _stopSession();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_stopping || !_pending.IsCompleted || _indicator is null) return;
        try
        {
            await BeginCapture(_indicator.Handle);
        }
        catch (CaptureUnavailableException)
        {
            // Блокировка, смена рабочего стола или перекрытая панель: пропускаем этот интервал.
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception ex)
        {
            _reportError("Не удалось сохранить снимок: " + ex.Message);
        }
    }

    private Task BeginCapture(IntPtr? indicator)
    {
        if (_pending.IsCompleted)
        {
            var token = _cancel.Token;
            _pending = Task.Run(() => CaptureScreensAsync(indicator, token), token);
        }
        return _pending;
    }

    private async Task CaptureScreensAsync(IntPtr? indicator, CancellationToken token)
    {
        ValidateDesktop(indicator, token);
        var monitors = EnumerateMonitors();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int index = 0; index < monitors.Count; index++)
        {
            var bounds = monitors[index].Bounds;
            ValidateDesktop(indicator, token);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            // Сеанс мог измениться между проверкой и копированием. До записи проверяем ещё раз.
            ValidateDesktop(indicator, token);
            using var png = new System.IO.MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            await _store.SavePngAsync($"{stamp}_mon{index + 1}.png", png.ToArray(), token).ConfigureAwait(false);
        }
    }

    public Task StopAsync()
    {
        _ui.VerifyAccess();
        if (_shutdown is not null) return _shutdown;
        _stopping = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        _cancel.Cancel();
        _shutdown = FinishStoppingAsync();
        return _shutdown;
    }

    private async Task FinishStoppingAsync()
    {
        try { await _pending; }
        // Вызывающая кнопка или обработчик таймера уже получают исключение снимка.
        catch (Exception) { }
        finally
        {
            CloseIndicator();
            _cancel.Dispose();
        }
    }

    private static List<(Rectangle Bounds, bool Primary)> EnumerateMonitors()
    {
        var screens = new List<(Rectangle Bounds, bool Primary)>();
        Win32Exception? readError = null;
        bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(handle, ref info))
            {
                readError = new Win32Exception(Marshal.GetLastWin32Error());
                return false;
            }
            var rect = info.Bounds;
            var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width > 0 && bounds.Height > 0)
                screens.Add((bounds, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        if (readError is not null) throw readError;
        if (!success || screens.Count == 0)
            throw new InvalidOperationException("Не удалось получить список экранов.");
        // Основной монитор получает номер 1, остальные упорядочиваются по расположению.
        screens.Sort((a, b) =>
        {
            int primary = b.Primary.CompareTo(a.Primary);
            if (primary != 0) return primary;
            int x = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return x != 0 ? x : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });
        return screens;
    }

    private static void ValidateDesktop(IntPtr? indicator, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsInteractiveDesktop())
            throw new CaptureUnavailableException("Снимок доступен только в активном разблокированном сеансе.");
        if (indicator is IntPtr handle && (handle == IntPtr.Zero || !IsIndicatorExposed(handle)))
            throw new CaptureUnavailableException("Панель управления снимками должна быть видна на рабочем столе.");
    }

    private static bool IsInteractiveDesktop()
    {
        // Проверка WTS отсекает отключённые RDP-сеансы и сеансы других вошедших пользователей.
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, 8, out IntPtr state, out int size))
            return false;
        try
        {
            if (size < sizeof(int) || Marshal.ReadInt32(state) != 0) return false; // WTSActive
        }
        finally { WTSFreeMemory(state); }

        IntPtr desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2, name, name.Capacity * sizeof(char), out _)
                && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    private static bool IsIndicatorExposed(IntPtr handle)
    {
        // В том числе пропускаем виртуальные рабочие столы, на которых панели нет.
        if (!IsWindowVisible(handle) || IsIconic(handle) ||
            DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0 ||
            !GetWindowRect(handle, out var rect))
            return false;

        var middle = new NativePoint
        {
            X = rect.Left + (rect.Right - rect.Left) / 2,
            Y = rect.Top + (rect.Bottom - rect.Top) / 2
        };
        return GetAncestor(WindowFromPoint(middle), 2) == handle; // GA_ROOT
    }

    private sealed class CaptureUnavailableException(string message) : InvalidOperationException(message) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Bounds, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr state);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int type, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int bytes);
}

internal sealed class CaptureIndicator : Window
{
    private readonly Action _stop;
    private readonly Controls.TextBlock _status = new() { FontWeight = FontWeights.SemiBold };
    private readonly Controls.Button _openDashboard = new();
    private Action? _showDashboard;
    private bool _closingProgrammatically;
    private bool _closed;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public CaptureIndicator(Action stop)
    {
        _stop = stop;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = true;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var content = new Controls.StackPanel { Margin = new Thickness(12, 8, 12, 10) };
        content.Children.Add(_status);
        _openDashboard.Content = "Открыть окно управления";
        _openDashboard.Padding = new Thickness(8, 4, 8, 4);
        _openDashboard.Margin = new Thickness(0, 6, 0, 0);
        _openDashboard.Click += (_, _) => _showDashboard?.Invoke();
        content.Children.Add(_openDashboard);
        var stopButton = new Controls.Button
        {
            Content = "Остановить сеанс",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0)
        };
        stopButton.Click += (_, _) => _stop();
        content.Children.Add(stopButton);
        Content = content;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - ActualWidth - 12);
            Top = area.Top + 12;
        };
        Closed += (_, _) => _closed = true;
        Closing += (_, e) =>
        {
            if (_closingProgrammatically) return;
            e.Cancel = true;
            // Закрытие самой панели всегда останавливает сбор. Повторный Close
            // переносим за пределы текущего события Closing.
            Dispatcher.BeginInvoke(_stop);
        };
    }

    public void SetMode(int? minutes, Action? showDashboard)
    {
        Title = minutes.HasValue ? "PC Monitor Tool — снимки экрана" : "PC Monitor Tool — учёт времени";
        _status.Text = minutes.HasValue ? $"●  Снимки экрана каждые {minutes} мин." : "●  Идёт учёт времени";
        _status.Foreground = minutes.HasValue ? Media.Brushes.DarkRed : Media.Brushes.DarkGreen;
        _showDashboard = showDashboard;
        _openDashboard.Visibility = showDashboard is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void CloseProgrammatically()
    {
        _closingProgrammatically = true;
        if (!_closed) Close();
    }
}
