using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCMonitorTool;

internal sealed class UsbWatcher(bool trackFiles, string dataRoot, Action<string, object> emit, Action<string> reportError) : IDisposable
{
    // Из WMI-потока только ставим уведомление в очередь. Словарь меняет один рабочий поток.
    private readonly record struct Signal(string? Drive = null, bool Removed = false, bool Initial = false);
    private sealed class Volume
    {
        public FileSystemWatcher? Watcher;
        public volatile bool NeedsRestart;
        public volatile bool Active = true;
    }

    private readonly Channel<Signal> changes = Channel.CreateBounded<Signal>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Dictionary<string, Volume> volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string configuredDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
    private ManagementEventWatcher? events;
    private System.Threading.Timer? rescanTimer;
    private Task? worker;
    private int disposed;
    private int missed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (worker != null) return;
        Enqueue(new Signal(Initial: true));
        worker = Task.Run(ProcessEventsAsync);
        try
        {
            events = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_VolumeChangeEvent WHERE EventType=2 OR EventType=3"));
            events.EventArrived += OnVolumeChange;
            events.Stopped += OnStopped;
            events.Start();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            reportError($"USB: уведомления WMI недоступны, останется сверка раз в минуту. {ex.Message}");
        }

        // Сверяем список и после переполнения буфера, отказа WMI или поздней готовности диска.
        rescanTimer = new System.Threading.Timer(_ => Enqueue(new Signal()), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void OnVolumeChange(object sender, EventArrivedEventArgs e)
    {
        using var item = e.NewEvent;
        try
        {
            var drive = Convert.ToString(item["DriveName"])?.ToUpperInvariant();
            if (drive is { Length: 2 } && drive[0] is >= 'A' and <= 'Z' && drive[1] == ':')
                Enqueue(new Signal(drive, Convert.ToUInt32(item["EventType"]) == 3));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or FormatException)
        {
            Interlocked.Increment(ref missed);
            Enqueue(new Signal());
        }
    }

    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (Volatile.Read(ref disposed) == 0)
            reportError($"USB: подписка WMI завершена ({e.Status}). Работает сверка раз в минуту.");
    }

    private void Enqueue(Signal signal)
    {
        if (Volatile.Read(ref disposed) == 0 && !changes.Writer.TryWrite(signal))
            Interlocked.Increment(ref missed);
    }

    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (var change in changes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref disposed) != 0) break;
                try
                {
                    var skipped = Interlocked.Exchange(ref missed, 0);
                    if (skipped != 0)
                    {
                        emit("files", new { kind = "watcher.gap", source = "volumeEvents", count = skipped });
                        Scan(false);
                    }

                    if (change.Drive is null) Scan(change.Initial);
                    else if (change.Removed) Detach(change.Drive, true);
                    else Refresh(change.Drive, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    reportError($"USB: не удалось проверить накопитель. {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var drive in volumes.Keys.ToArray()) Detach(drive, false);
        }
    }

    private void Scan(bool initial)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (Volatile.Read(ref disposed) != 0) break;
            var letter = drive.Name.TrimEnd('\\');
            if (Refresh(letter, initial)) seen.Add(letter);
        }
        foreach (var old in volumes.Keys.Where(k => !seen.Contains(k)).ToArray()) Detach(old, true);
    }

    private bool Refresh(string letter, bool initial)
    {
        var root = letter + "\\";
        var driveInfo = new DriveInfo(root);
        // Сетевые, системные диски и карты памяти без подтверждённого USB-интерфейса пропускаем.
        if (driveInfo.DriveType != DriveType.Removable || !driveInfo.IsReady || !IsUsb(letter))
        {
            Detach(letter, true);
            return false;
        }

        if (!volumes.TryGetValue(letter, out var volume))
        {
            volumes.Add(letter, volume = new Volume());
            emit("files", new { kind = initial ? "volume.present" : "volume.arrived", drive = letter });
        }
        if (!trackFiles || (volume.Watcher != null && !volume.NeedsRestart)) return true;

        volume.Watcher?.Dispose();
        volume.Watcher = null;
        volume.NeedsRestart = false;
        try
        {
            // Сопоставляем папку также при второй букве того же тома или коротком имени 8.3.
            var actualRoot = GetPhysicalDirectory(root);
            var actualData = GetPhysicalDirectory(configuredDataRoot);
            if (actualRoot is null || actualData is null)
                throw new IOException("Не удалось сопоставить том и папку данных.");
            var deviceRoot = actualRoot.TrimEnd('\\');
            var deviceData = actualData.TrimEnd('\\');
            // Буква SUBST может указывать прямо внутрь хранилища.
            if (InTree(deviceRoot, deviceData)) return true;
            string? localData = InTree(deviceData, deviceRoot)
                ? Path.Combine(root, deviceData[(deviceRoot.Length + 1)..]) : null;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            volume.Watcher = watcher;

            bool Excluded(string path) => InTree(path, configuredDataRoot) || localData != null && InTree(path, localData);

            void Record(object sender, FileSystemEventArgs e)
            {
                if (!volume.Active || Volatile.Read(ref disposed) != 0 || sender != volume.Watcher) return;
                // Не порождаем новые записи при записи/ротации собственного журнала.
                // При переносе через границу исключаем событие целиком с обоими именами.
                if (Excluded(e.FullPath) || e is RenamedEventArgs moved && Excluded(moved.OldFullPath)) return;
                long? size = null;
                if (e.ChangeType != WatcherChangeTypes.Deleted)
                {
                    try
                    {
                        var info = new FileInfo(e.FullPath);
                        if (info.Exists) size = info.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        // Файл уже исчез, занят или недоступен. Сохраняем само событие.
                    }
                }
                if (!volume.Active || Volatile.Read(ref disposed) != 0) return;
                emit("files", new
                {
                    kind = "file." + e.ChangeType.ToString().ToLowerInvariant(),
                    drive = letter, name = Path.GetFileName(e.Name),
                    oldName = e is RenamedEventArgs rename ? Path.GetFileName(rename.OldName) : null,
                    sizeBytes = size
                });
            }

            watcher.Created += Record;
            watcher.Changed += Record;
            watcher.Deleted += Record;
            watcher.Renamed += Record;
            watcher.Error += (sender, _) =>
            {
                if (!volume.Active || Volatile.Read(ref disposed) != 0 || sender != volume.Watcher) return;
                volume.NeedsRestart = true;
                emit("files", new { kind = "watcher.gap", source = "fileEvents", drive = letter });
            };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.ComponentModel.Win32Exception)
        {
            volume.NeedsRestart = true;
            emit("files", new { kind = "watcher.gap", source = "fileEvents", drive = letter });
            reportError($"USB {letter} журнал изменений недоступен. {ex.Message}");
        }
        return true;
    }

    private void Detach(string letter, bool record)
    {
        if (!volumes.Remove(letter, out var volume)) return;
        volume.Active = false;
        volume.Watcher?.Dispose();
        if (record && Volatile.Read(ref disposed) == 0)
            emit("files", new { kind = "volume.removed", drive = letter });
    }

    private static bool InTree(string path, string directory) =>
        path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == directory.Length || path[directory.Length] == Path.DirectorySeparatorChar);

    private static string? GetPhysicalDirectory(string path)
    {
        // Дескриптор каталога позволяет получить нормализованное имя с GUID тома.
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(260);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
        }
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint length, uint flags);

    private static bool IsUsb(string letter)
    {
        // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery.
        // Том открываем без прав чтения/записи; запрашиваем только описание устройства.
        using var handle = CreateFile(@"\\.\" + letter, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var descriptor = new byte[1024];
        return DeviceIoControl(handle, 0x002D1400, new byte[12], 12, descriptor, descriptor.Length,
                   out var returned, IntPtr.Zero)
               && returned >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28, 4)) == 7;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] query, int querySize,
        [Out] byte[] descriptor, int descriptorSize, out int returned, IntPtr overlapped);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        rescanTimer?.Dispose();
        changes.Writer.TryComplete();
        if (events != null)
        {
            events.EventArrived -= OnVolumeChange;
            events.Stopped -= OnStopped;
            try { events.Stop(); }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
            finally { events.Dispose(); }
        }
        worker?.GetAwaiter().GetResult();
    }
}
