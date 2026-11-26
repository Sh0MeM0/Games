using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PCMonitorTool;

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int FirstId = 0x5270;
    private const int SecondId = 0x5271;
    private const uint AltCtrlShiftNoRepeat = 0x0001 | 0x0002 | 0x0004 | 0x4000;
    private const uint VkF1 = 0x70;
    private readonly Action _show;
    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private int _activeId;
    private int _functionKey;
    private bool _disposed;

    public HotkeyManager(Window window, Action show)
    {
        window.Dispatcher.VerifyAccess();
        _show = show;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("Не удалось подключиться к окну управления.");
        _source.AddHook(WindowProc);
    }

    public bool Register(int functionKey)
    {
        _source.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (functionKey is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(functionKey));
        if (functionKey == _functionKey)
            return true;

        // Сохраняем прежнее сочетание, если новое уже занято другим приложением.
        int nextId = _activeId == FirstId ? SecondId : FirstId;
        uint virtualKey = VkF1 + (uint)(functionKey - 1);
        if (!RegisterHotKey(_hwnd, nextId, AltCtrlShiftNoRepeat, virtualKey))
            return false;
        if (_activeId != 0)
            UnregisterHotKey(_hwnd, _activeId);
        _activeId = nextId;
        _functionKey = functionKey;
        return true;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && _activeId != 0 && message == WmHotkey && wParam.ToInt32() == _activeId)
        {
            handled = true;
            _source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, _show);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _source.Dispatcher.VerifyAccess();
        _disposed = true;
        if (_activeId != 0)
            UnregisterHotKey(_hwnd, _activeId);
        if (!_source.IsDisposed)
            _source.RemoveHook(WindowProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
