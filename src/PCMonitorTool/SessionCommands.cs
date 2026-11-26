using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security;
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
    private const string PrimaryAlias = "pcmtools.exe";
    private const string ManagedTarget = "PCMonitorTool.ManagedTarget.v1";
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AliasKey(PrimaryAlias));
            return SamePath(ParsePath(key?.GetValue(null)), Environment.ProcessPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Запрет чтения App Paths не мешает изменять обычные настройки.
            return false;
        }
    }

    public static void ConfigureRegistration(bool autoStart) => SaveWithRegistration(autoStart, null, () => { });
    public static void ConfigureRunCommand(bool enable) => SaveWithRegistration(null, enable, () => { });
    public static void RemoveRegistrations() => SaveWithRegistration(false, false, () => { });

    private sealed record RegistryValue(object Data, RegistryValueKind Kind);
    private sealed record Change(string Key, string Name, RegistryValue? Before, RegistryValue? After);

    // Проверяем обе команды до первой записи. При сбое возвращаем прежние пути и типы значений.
    // null означает, что пользователь не менял соответствующую настройку.
    public static void SaveWithRegistration(bool? autoStart, bool? runCommand, Action persist)
    {
        if (autoStart is null && runCommand is null) { persist(); return; }
        string path = ExecutablePath();
        using var identity = WindowsIdentity.GetCurrent();
        using var gate = new Mutex(false, @"Global\PCMonitorTool.Registration." + identity.User?.Value);
        try
        {
            try
            {
                if (!gate.WaitOne(TimeSpan.FromSeconds(3)))
                    throw new IOException("Другой сеанс меняет регистрацию программы. Повторите сохранение.");
            }
            catch (AbandonedMutexException) { } // Windows уже передала владение этому потоку.
            try { ApplyRegistration(autoStart, runCommand, path, persist); }
            finally { gate.ReleaseMutex(); }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Windows запретила изменение регистрации для текущего пользователя.", ex);
        }
    }

    private static void ApplyRegistration(bool? autoStart, bool? runCommand, string path, Action persist)
    {
        var changes = new List<Change>();
        if (autoStart.HasValue)
            PlanAutoStart(changes, autoStart.Value, path);
        if (runCommand.HasValue)
        {
            PlanAlias(changes, PrimaryAlias, runCommand.Value, path, required: true);
            PlanAlias(changes, ExeName, runCommand.Value, path, required: false);
        }

        var applied = new Stack<Change>();
        try
        {
            foreach (var change in changes)
            {
                Apply(change, undo: false);
                applied.Push(change);
            }
            persist();
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            while (applied.TryPop(out var change))
            {
                try { Apply(change, undo: true); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 1)
                throw new AggregateException("Не удалось полностью восстановить регистрацию. " + original.Message, failures);
            throw;
        }
    }

    private static string AliasKey(string alias) => AppPathsKey + @"\" + alias;

    private static void PlanAlias(List<Change> changes, string alias, bool enable, string path, bool required)
    {
        string keyPath = AliasKey(alias);
        using var userKey = Registry.CurrentUser.OpenSubKey(keyPath);
        var current = ReadValue(userKey, "");
        var owner = ReadValue(userKey, ManagedTarget);
        object? ownerPath = owner?.Data;
        // Учитываем системную регистрацию, чтобы новая HKCU-запись не перекрыла чужую команду.
        using var systemKey = enable && current is null ? Registry.LocalMachine.OpenSubKey(keyPath) : null;
        var effective = current ?? ReadValue(systemKey, "");
        var status = Classify(effective?.Data, path, ownerPath);
        if (enable && status == Target.Foreign)
        {
            if (required)
                throw new InvalidOperationException($"Команда {Path.GetFileNameWithoutExtension(alias)} в Win+R уже открывает «{effective?.Data}». Снимите флажок команды, чтобы сохранить остальные настройки. Панель можно открыть полным путём к pcmtool.exe.");
            return; // Старая команда необязательна. Другой программе оставляем её регистрацию целиком.
        }

        if (!enable && (current is null || status == Target.Foreign ||
            (status == Target.Stale && !SamePath(ParsePath(current.Data), ParsePath(ownerPath)))))
            return;
        Plan(changes, keyPath, "", current, enable ? path : null);
        if (enable || SamePath(ParsePath(ownerPath), ParsePath(current?.Data)))
            Plan(changes, keyPath, ManagedTarget, owner, enable ? path : null);
    }

    private static void PlanAutoStart(List<Change> changes, bool enable, string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var current = ReadValue(key, RunValue);
        // Отдельное имя Run даже при совпадении команд никогда не используется как App Paths.
        var status = Classify(current?.Data, path, owner: null);
        if (status == Target.Foreign)
        {
            if (enable) throw new InvalidOperationException($"Запись автозапуска «{RunValue}» уже открывает «{current?.Data}». Отключите этот флажок или проверьте прежнюю установку.");
            return;
        }
        if (!enable && status == Target.Stale) return;
        Plan(changes, RunKey, RunValue, current, enable ? "\"" + path + "\"" : null);
    }

    private enum Target { Available, Current, Previous, Stale, Foreign }

    private static Target Classify(object? value, string path, object? owner)
    {
        if (value is null) return Target.Available;
        string? registered = ParsePath(value);
        if (registered is null) return Target.Foreign;
        if (SamePath(registered, path)) return Target.Current;
        if (!string.Equals(Path.GetFileName(registered), ExeName, StringComparison.OrdinalIgnoreCase)) return Target.Foreign;
        try
        {
            if ((File.GetAttributes(registered) & FileAttributes.Directory) != 0) return Target.Foreign;
            // У старых версий нет нашей метки. Проверяем сведения продукта, а не только имя exe.
            return IsProductBinary(registered) ? Target.Previous : Target.Foreign;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Недоступный сетевой путь сам по себе не доказывает, что установка удалена.
            if (registered.StartsWith(@"\\", StringComparison.Ordinal) && !SamePath(ParsePath(owner), registered))
                return Target.Foreign;
            return Target.Stale;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception)
        {
            return Target.Foreign;
        }
    }

    private static bool IsProductBinary(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return string.Equals(info.ProductName, "PC Monitor Tool", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(info.InternalName, "pcmtool.dll", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(info.OriginalFilename, ExeName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(info.OriginalFilename, "pcmtool.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParsePath(object? value)
    {
        if (value is not string text) return null;
        text = Environment.ExpandEnvironmentVariables(text.Trim());
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        if (text.Contains('"') || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(text)) return null;
        try { return Path.GetFullPath(text); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static RegistryValue? ReadValue(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : new RegistryValue(value, key!.GetValueKind(name));
    }

    private static void Plan(List<Change> changes, string key, string name, RegistryValue? before, string? text)
    {
        RegistryValue? after = text is null ? null : new RegistryValue(text, RegistryValueKind.String);
        if (before != after) changes.Add(new Change(key, name, before, after));
    }

    private static void Apply(Change change, bool undo)
    {
        var expected = undo ? change.After : change.Before;
        var next = undo ? change.Before : change.After;
        using var key = next is null
            ? Registry.CurrentUser.OpenSubKey(change.Key, writable: true)
            : Registry.CurrentUser.CreateSubKey(change.Key);
        if (ReadValue(key, change.Name) != expected)
            throw new IOException("Запись реестра изменилась во время сохранения. Откройте настройки заново.");
        if (key is null) return;
        if (next is null) key.DeleteValue(change.Name, throwOnMissingValue: false);
        else key.SetValue(change.Name, next.Data, next.Kind);
        // Пустой ключ безопасно оставить: удаление целого ключа могло бы затронуть
        // значения, которые добавил другой установщик между проверкой и удалением.
    }

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
