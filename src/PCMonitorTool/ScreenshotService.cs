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
using System.Windows.Threading;
using Controls = System.Windows.Controls;

namespace PCMonitorTool;

/// <summary>Снимки открытого рабочего стола текущего интерактивного сеанса.</summary>
public sealed class ScreenshotService
{
    private readonly DataStore _store;
    private readonly Action<string> _reportError;
    private readonly Action _onSourceClosed;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<SharedScreenCapture> _sources = new();
    private Task<bool>? _setup;
    private Task _manual = Task.CompletedTask;
    private Task _periodic = Task.CompletedTask;
    private Task? _shutdown;
    private DispatcherTimer? _timer;
    private bool _stopping;
    private bool _discardingSources;

    public ScreenshotService(DataStore store, Action<string> reportError, Action onSourceClosed)
    {
        _store = store;
        _reportError = reportError;
        _onSourceClosed = onSourceClosed;
    }

    // Ручной снимок всех экранов вызывает только кнопка после явного подтверждения.
    public Task CaptureNowAsync()
    {
        _ui.VerifyAccess();
        if (_stopping) throw new InvalidOperationException("Сеанс завершён.");
        if (_setup is { IsCompleted: false })
            throw new InvalidOperationException("Сначала завершите выбор источников.");
        if (_manual.IsCompleted)
        {
            var token = _cancel.Token;
            _manual = Task.Run(() => CaptureManualAsync(token), token);
        }
        return _manual;
    }

    // Источники периодических снимков предоставляет только системный диалог Windows.
    public Task<bool> StartAutomaticAsync(Window owner, int minutes)
    {
        _ui.VerifyAccess();
        if (_stopping || _setup != null)
            throw new InvalidOperationException("Сеанс завершён или выбор уже начат.");
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        _setup = SelectSourcesAsync(owner, minutes, _cancel.Token);
        return _setup;
    }

    private async Task<bool> SelectSourcesAsync(Window owner, int minutes, CancellationToken token)
    {
        // Публикуем задачу до входа в диалоги: StopAsync может быть вызван из вложенного цикла UI.
        await Task.Yield();
        try
        {
            while (true)
            {
                ValidateDesktop(token);
                var source = await SharedScreenCapture.PickAsync(owner, SourceClosed, token);
                if (source != null) _sources.Add(source);
                ValidateDesktop(token);
                if (source == null)
                {
                    if (_sources.Count == 0) return false;
                    break;
                }

                var choice = ChooseNext(owner, _sources.Count, token);
                token.ThrowIfCancellationRequested();
                if (choice == SourceChoice.Cancel) return false;
                if (choice == SourceChoice.Finish) break;
            }
            ValidateDesktop(token);
            _timer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            {
                Interval = TimeSpan.FromMinutes(minutes)
            };
            _timer.Tick += OnTick;
            _timer.Start();
            return true;
        }
        finally
        {
            if (_timer == null) DisposeSources();
        }
    }

    private enum SourceChoice { Cancel, Finish, Add }

    private static SourceChoice ChooseNext(Window owner, int count, CancellationToken token)
    {
        var dialog = new Window
        {
            Owner = owner, Title = "Источники автоснимков",
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight
        };
        var panel = new Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new Controls.TextBlock { Text = $"Выбрано источников: {count}. Включить автоснимки?" });
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(buttons);
        dialog.Content = panel;
        SourceChoice result = SourceChoice.Cancel;
        void Add(string text, SourceChoice choice)
        {
            var button = new Controls.Button
            {
                Content = text, IsDefault = choice == SourceChoice.Finish, IsCancel = choice == SourceChoice.Cancel,
                Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = choice != SourceChoice.Cancel; };
            buttons.Children.Add(button);
        }
        Add("Готово", SourceChoice.Finish);
        Add("Выбрать ещё", SourceChoice.Add);
        Add("Отмена", SourceChoice.Cancel);
        using var close = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        token.ThrowIfCancellationRequested();
        dialog.ShowDialog();
        return result;
    }

    private void SourceClosed()
    {
        // SharedScreenCapture доставляет уведомление на диспетчер окна владельца.
        _ui.VerifyAccess();
        if (_stopping || _discardingSources) return;
        _ = StopAsync();
        _onSourceClosed();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_stopping || !_periodic.IsCompleted || !_manual.IsCompleted) return;
        try
        {
            var token = _cancel.Token;
            var sources = _sources.ToArray();
            _periodic = Task.Run(() => CaptureSelectedAsync(sources, token), token);
            await _periodic;
        }
        catch (CaptureUnavailableException) { /* Защищённый или отключённый рабочий стол. */ }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception ex) { if (!_stopping) _reportError("Не удалось сохранить автоснимок: " + ex.Message); }
    }

    private async Task CaptureSelectedAsync(SharedScreenCapture[] sources, CancellationToken token)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int i = 0; i < sources.Length; i++)
        {
            ValidateDesktop(token);
            var png = await sources[i].GetPngAsync(token).ConfigureAwait(false);
            ValidateDesktop(token);
            if (png != null)
                await _store.SavePngAsync($"{stamp}_mon{i + 1}.png", png, token).ConfigureAwait(false);
        }
    }

    private async Task CaptureManualAsync(CancellationToken token)
    {
        ValidateDesktop(token);
        var monitors = EnumerateMonitors();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int i = 0; i < monitors.Count; i++)
        {
            var bounds = monitors[i].Bounds;
            ValidateDesktop(token);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            ValidateDesktop(token);
            using var png = new System.IO.MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            ValidateDesktop(token);
            await _store.SavePngAsync($"{stamp}_mon{i + 1}.png", png.ToArray(), token).ConfigureAwait(false);
        }
    }

    public Task StopAsync()
    {
        _ui.VerifyAccess();
        if (_shutdown != null) return _shutdown;
        _stopping = true;
        _timer?.Stop();
        if (_timer != null) _timer.Tick -= OnTick;
        _cancel.Cancel();
        // Сразу прекращаем системную трансляцию, даже если на диске завершается запись.
        DisposeSources();
        _shutdown = FinishStoppingAsync();
        return _shutdown;
    }

    private void DisposeSources()
    {
        _discardingSources = true;
        try
        {
            foreach (var source in _sources)
            {
                try { source.Dispose(); }
                catch (Exception ex) { _reportError("Не удалось закрыть системный источник: " + ex.Message); }
            }
            _sources.Clear();
        }
        finally { _discardingSources = false; }
    }

    private async Task FinishStoppingAsync()
    {
        // После отмены ждём также поздний результат системного выбора и его освобождение.
        try { await Task.WhenAll(_setup ?? Task.CompletedTask, _manual, _periodic); }
        catch (Exception) { /* Ошибку операции обрабатывает её непосредственный инициатор. */ }
        finally { DisposeSources(); _cancel.Dispose(); }
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

    private static void ValidateDesktop(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsInteractiveDesktop())
            throw new CaptureUnavailableException("Снимок доступен только в активном разблокированном сеансе.");
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

    private sealed class CaptureUnavailableException(string message) : InvalidOperationException(message) { }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
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
}
