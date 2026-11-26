using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PCMonitorTool;

/// <summary>Сеанс учёта времени для текущего интерактивного пользователя.</summary>
public sealed class MonitorService
{
    private readonly DataStore _data;
    private readonly MonitorSettings _settings;
    private readonly Action<string> _reportError;
    private readonly Action? _onUnexpectedStop;
    private readonly object _lifecycle = new();
    private CancellationTokenSource? _stop;
    private SessionState? _desktop;
    private Task _completion = Task.CompletedTask;

    public MonitorService(DataStore data, MonitorSettings settings, Action<string> reportError, Action? onUnexpectedStop = null)
    {
        _data = data;
        _settings = settings;
        _reportError = reportError;
        _onUnexpectedStop = onUnexpectedStop;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (!_completion.IsCompleted)
                throw new InvalidOperationException("Предыдущий сеанс ещё не завершён.");

            var stop = new CancellationTokenSource();
            var desktop = new SessionState();
            _desktop = desktop;
            _stop = stop;
            _completion = Task.Run(async () =>
            {
                try { await RunAsync(stop.Token, desktop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { _reportError($"Учёт времени остановлен. {ex.Message}"); }
                finally
                {
                    bool unexpected;
                    lock (_lifecycle)
                    {
                        unexpected = !stop.IsCancellationRequested;
                        _stop = null;
                        _desktop = null;
                        stop.Dispose();
                    }
                    if (unexpected) _onUnexpectedStop?.Invoke();
                }
            });
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stop is { IsCancellationRequested: false } source)
            {
                // Фиксируем границу до возврата в UI; медленные обработчики отмены выполняются в фоне.
                _desktop!.MarkStopped();
                _ = source.CancelAsync();
            }
            return _completion;
        }
    }

    private async Task RunAsync(CancellationToken outerToken, SessionState desktop)
    {
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var token = sessionStop.Token;
        using var identity = WindowsIdentity.GetCurrent();
        using var currentProcess = Process.GetCurrentProcess();
        var owner = identity.User ?? throw new InvalidOperationException("Не удалось определить SID пользователя.");
        int sessionId = currentProcess.SessionId;
        var queue = new EventQueue(_data, _reportError);
        var writing = queue.DrainAsync();
        var traces = new List<ManagementEventWatcher>();
        Task sampling = Task.CompletedTask;

        // Обработчики WMI/FSW лишь помещают события в ограниченную очередь.
        void Publish(string stream, object data)
        {
            if (!token.IsCancellationRequested && desktop.StoppedAt is null && !desktop.Paused)
                queue.Post(stream, data);
        }

        var usb = new UsbWatcher(_settings.UsbFileEvents, _data.Root, Publish, _reportError);
        try
        {
            if (token.IsCancellationRequested) return;
            SystemEvents.SessionSwitch += desktop.OnSession;
            SystemEvents.PowerModeChanged += desktop.OnPower;
            sampling = SampleAsync(owner, sessionId, desktop, queue, token);
            foreach (bool isStart in new[] { true, false })
            {
                if (token.IsCancellationRequested) break;
                string className = isStart ? "Win32_ProcessStartTrace" : "Win32_ProcessStopTrace";
                string kind = isStart ? "process_start" : "process_stop";
                var watcher = new ManagementEventWatcher(
                    $"SELECT * FROM {className} WHERE SessionID = {sessionId.ToString(CultureInfo.InvariantCulture)}");
                traces.Add(watcher);
                watcher.EventArrived += (_, e) =>
                {
                    using var entry = e.NewEvent;
                    if (token.IsCancellationRequested || desktop.Paused) return;
                    try
                    {
                        // Отказываемся от события, если не подтверждены И сеанс, И владелец.
                        if (entry["Sid"] is not byte[] sid || !owner.Equals(new SecurityIdentifier(sid, 0)) ||
                            entry["SessionID"] is not uint sidNumber || sidNumber != (uint)sessionId ||
                            entry["ProcessID"] is not uint pid || entry["ProcessName"] is not string name)
                            return;

                        Publish("apps", new { kind, process = Path.GetFileName(name), pid, observedUtc = DateTimeOffset.UtcNow });
                    }
                    catch (Exception ex) when (ex is ArgumentException or ManagementException or COMException)
                    {
                        queue.MarkLost("apps");
                    }
                };
                watcher.Stopped += (_, e) =>
                {
                    if (!token.IsCancellationRequested)
                        _reportError($"Доставка событий {className} прервана ({e.Status}).");
                };
                try { watcher.Start(); }
                catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
                {
                    _reportError($"Источник {className} недоступен. {ex.Message}");
                }
            }
            if (!token.IsCancellationRequested)
            {
                try { usb.Start(); }
                catch (Exception ex) { _reportError($"Учёт USB недоступен. {ex.Message}"); }
            }

            await sampling.ConfigureAwait(false);
        }
        finally
        {
            sessionStop.Cancel();
            try { await sampling.ConfigureAwait(false); }
            catch (Exception ex) { _reportError($"Не удалось завершить интервал учёта. {ex.Message}"); }
            SystemEvents.SessionSwitch -= desktop.OnSession;
            SystemEvents.PowerModeChanged -= desktop.OnPower;
            foreach (var trace in traces)
            {
                try { trace.Stop(); }
                catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException) { }
                finally { trace.Dispose(); }
            }
            try { usb.Dispose(); }
            finally
            {
                queue.Complete();
                await writing.ConfigureAwait(false);
            }
        }
    }

    private async Task SampleAsync(SecurityIdentifier owner, int sessionId, SessionState desktop,
        EventQueue queue, CancellationToken token)
    {
        var period = TimeSpan.FromSeconds(Math.Clamp(_settings.SampleSeconds, 1, 86400));
        using var timer = new PeriodicTimer(period);
        if (desktop.StoppedAt is not null || token.IsCancellationRequested) return;
        var previous = Observe(owner, sessionId, desktop);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var current = desktop.StoppedAt ?? Observe(owner, sessionId, desktop);
                if (desktop.StoppedAt is { } end && end.Tick <= current.Tick)
                {
                    EndInterval(previous, end, period, queue);
                    return;
                }
                EndInterval(previous, current, period, queue);
                previous = current;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // При остановке учитываем неполный интервал, не опрашивая новое окно.
            EndInterval(previous, desktop.StoppedAt ?? TimePoint(desktop), period, queue);
        }
    }

    private sealed record Sample(DateTimeOffset Utc, long Tick, int Generation, double IdleSeconds,
        bool Paused, string? Process = null, uint Pid = 0);

    private static Sample TimePoint(SessionState desktop)
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        double idle = GetLastInputInfo(ref input)
            ? unchecked((uint)Environment.TickCount64 - input.Time) / 1000.0 : double.PositiveInfinity;
        return new Sample(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), desktop.Generation, idle, desktop.Paused);
    }

    private static Sample Observe(SecurityIdentifier owner, int sessionId, SessionState desktop)
    {
        var point = TimePoint(desktop);
        if (point.Paused || point.IdleSeconds >= 300) return point;
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint pid) == 0 ||
            !ProcessIdToSessionId(pid, out uint session) || session != (uint)sessionId)
            return point;

        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var access)) return point;
        using (access)
        {
            try
            {
                using var identity = new WindowsIdentity(access.DangerousGetHandle());
                if (identity.User is not { } user || !owner.Equals(user)) return point;
                var image = new StringBuilder(1024);
                int length = image.Capacity;
                if (!QueryFullProcessImageName(process, 0, image, ref length)) return point;
                return point with { Process = Path.GetFileName(image.ToString()), Pid = pid };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                return point;
            }
        }
    }

    private static void EndInterval(Sample previous, Sample current, TimeSpan period, EventQueue queue)
    {
        if (previous.Process is null || previous.Paused || current.Paused ||
            previous.Generation != current.Generation || double.IsInfinity(current.IdleSeconds)) return;

        double elapsed = Stopwatch.GetElapsedTime(previous.Tick, current.Tick).TotalSeconds;
        // После сна, блокировки или перевода системных часов интервал считается неизвестным.
        if (elapsed > period.TotalSeconds + Math.Max(3, period.TotalSeconds / 4) ||
            Math.Abs((current.Utc - previous.Utc).TotalSeconds - elapsed) > 2) return;
        double active = Math.Round(elapsed - Math.Max(0, current.IdleSeconds - 300), 3);
        if (active <= 0) return;
        queue.Post("apps", new
        {
            kind = "foreground", process = previous.Process, pid = previous.Pid,
            startedUtc = previous.Utc, endedUtc = previous.Utc.AddSeconds(active), seconds = active
        });
    }

    private sealed class SessionState
    {
        private int _paused, _generation;
        private Sample? _stoppedAt;
        public Sample? StoppedAt => Volatile.Read(ref _stoppedAt);
        public void MarkStopped() => Interlocked.CompareExchange(ref _stoppedAt, TimePoint(this), null);
        public bool Paused => Volatile.Read(ref _paused) != 0;
        public int Generation => Volatile.Read(ref _generation);

        private void Change(int bit, bool pause)
        {
            if (pause) Interlocked.Or(ref _paused, bit);
            else Interlocked.And(ref _paused, ~bit);
            Interlocked.Increment(ref _generation);
        }

        public void OnSession(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock: Change(1, true); break;
                case SessionSwitchReason.SessionUnlock: Change(1, false); break;
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleDisconnect: Change(2, true); break;
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.ConsoleConnect: Change(2, false); break;
                case SessionSwitchReason.SessionLogoff: Change(4, true); break;
            }
        }

        public void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) Change(8, true);
            if (e.Mode == PowerModes.Resume) Change(8, false);
        }
    }

    private sealed class EventQueue(DataStore data, Action<string> reportError)
    {
        private readonly Channel<(string Stream, object Payload)> _queue =
            Channel.CreateBounded<(string, object)>(new BoundedChannelOptions(256)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
            });
        private long _lostApps, _lostFiles;
        private long _lastErrorTick = -60000;

        public void MarkLost(string stream)
        {
            if (stream == "apps") Interlocked.Increment(ref _lostApps);
            else Interlocked.Increment(ref _lostFiles);
        }

        public void Post(string stream, object payload)
        {
            if (!_queue.Writer.TryWrite((stream, payload))) MarkLost(stream);
        }

        public void Complete() => _queue.Writer.TryComplete();

        public async Task DrainAsync()
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await ReportDropsAsync(item.Stream).ConfigureAwait(false);
                if (!await WriteAsync(item.Stream, item.Payload).ConfigureAwait(false))
                    MarkLost(item.Stream);
            }
            await ReportDropsAsync("apps").ConfigureAwait(false);
            await ReportDropsAsync("files").ConfigureAwait(false);
        }

        private async Task ReportDropsAsync(string stream)
        {
            long count = stream == "apps" ? Interlocked.Exchange(ref _lostApps, 0) : Interlocked.Exchange(ref _lostFiles, 0);
            if (count == 0) return;
            if (!await WriteAsync(stream, new { kind = "events_dropped", count, observedUtc = DateTimeOffset.UtcNow })
                    .ConfigureAwait(false))
            {
                if (stream == "apps") Interlocked.Add(ref _lostApps, count);
                else Interlocked.Add(ref _lostFiles, count);
            }
        }

        private async Task<bool> WriteAsync(string stream, object item)
        {
            try { await data.LogAsync(stream, item).ConfigureAwait(false); return true; }
            catch (Exception ex)
            {
                if (Environment.TickCount64 - _lastErrorTick >= 30000)
                {
                    _lastErrorTick = Environment.TickCount64;
                    reportError($"Ошибка записи журнала. {ex.Message}");
                }
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { internal uint Size; internal uint Time; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}
