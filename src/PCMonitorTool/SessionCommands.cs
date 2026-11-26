using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PCMonitorTool;

public sealed class SessionCommands : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PC Monitor Tool";
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    private const string ExeName = "pcmtool.exe";
    private static readonly byte[] ShowCommand = "SHOW"u8.ToArray();
    private static readonly byte[] Accepted = [1];

    private readonly string _pipeName;
    private readonly Mutex? _instance;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _disposed;

    public bool IsPrimary => _instance is not null;

    public SessionCommands(string[] args)
    {
        // Любой повторный запуск только показывает панель. Аргументы не передаются.
        using var user = WindowsIdentity.GetCurrent();
        string sid = user.User?.Value
            ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        using var process = Process.GetCurrentProcess();
        _pipeName = $"PCMonitorTool.7343.{sid}.{process.SessionId}";
        var instance = new Mutex(false, @"Local\" + _pipeName, out bool created);
        if (created)
            _instance = instance;
        else
            instance.Dispose();
    }

    public async Task<bool> RequestShowAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
            return false;
        // Время ожидания также покрывает первый запуск WPF до создания сервера.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(ShowCommand, timeout.Token).ConfigureAwait(false);
            var ack = new byte[1];
            await client.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            return ack[0] == Accepted[0];
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            return false;
        }
    }

    public void Listen(Action onShow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onShow);
        if (!IsPrimary || _listener != null)
            throw new InvalidOperationException("Обработчик команд уже запущен или принадлежит другому экземпляру.");
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _listener = Task.Run(() => ServeAsync(dispatcher, onShow, _stop.Token));
    }

    private async Task ServeAsync(Dispatcher dispatcher, Action onShow, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stop);
                request.CancelAfter(TimeSpan.FromSeconds(1));
                var command = new byte[ShowCommand.Length];
                await pipe.ReadExactlyAsync(command, request.Token).ConfigureAwait(false);
                if (command.AsSpan().SequenceEqual(ShowCommand) && !dispatcher.HasShutdownStarted)
                {
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, onShow);
                    await pipe.WriteAsync(Accepted, request.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Клиент подключился, но не передал команду за отведённое время.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Следующее подключение должно работать и после обрыва канала.
                try { await Task.Delay(250, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public static bool IsRunCommandRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppPathsKey + @"\" + ExeName);
        return PointsTo(key?.GetValue(null), Environment.ProcessPath);
    }

    // Запись Run и команда Win+R независимы. Для каждой требуется явное включение.
    public static void ConfigureRegistration(bool autoStart)
    {
        string path = ExecutablePath();
        string command = "\"" + path + "\"";
        using var key = autoStart
            ? Registry.CurrentUser.CreateSubKey(RunKey)
            : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
            return;
        object? existing = key.GetValue(RunValue);
        if (autoStart)
        {
            if (existing != null && !PointsTo(existing, command))
                throw new InvalidOperationException("Имя записи автозапуска уже использует другой файл.");
            // Обычный запуск программы открывает видимую панель с остановленным сбором.
            key.SetValue(RunValue, command, RegistryValueKind.String);
        }
        else if (PointsTo(existing, command))
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static void ConfigureRunCommand(bool enable)
    {
        string path = ExecutablePath();
        using var parent = enable
            ? Registry.CurrentUser.CreateSubKey(AppPathsKey)
            : Registry.CurrentUser.OpenSubKey(AppPathsKey, writable: true);
        if (parent == null)
            return;
        using var key = enable ? parent.CreateSubKey(ExeName) : parent.OpenSubKey(ExeName, writable: true);
        if (key == null)
            return;
        object? existing = key.GetValue(null);
        if (enable)
        {
            if (existing != null && !PointsTo(existing, path))
                throw new InvalidOperationException("Команда pcmtool уже зарегистрирована для другого файла.");
            key.SetValue("", path, RegistryValueKind.String);
        }
        else if (PointsTo(existing, path))
        {
            key.DeleteValue("", throwOnMissingValue: false);
            if (key.ValueCount == 0 && key.SubKeyCount == 0)
                parent.DeleteSubKey(ExeName, throwOnMissingSubKey: false);
        }
    }

    public static void RemoveRegistrations()
    {
        ConfigureRegistration(false);
        ConfigureRunCommand(false);
    }

    private static bool PointsTo(object? value, string? expected) =>
        value is string text && expected is not null &&
        string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);

    private static string ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для регистрации запустите опубликованный файл pcmtool.exe.");
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        try { _listener?.GetAwaiter().GetResult(); }
        finally
        {
            _stop.Dispose();
            _instance?.Dispose();
        }
    }
}
