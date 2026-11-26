using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCMonitorTool;

public sealed record MonitorSettings
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMonitor");
    private static string UserFile => Path.Combine(DefaultDirectory, "settings.json");
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public string DataDirectory { get; init; } = DefaultDirectory;
    public int SampleSeconds { get; init; } = 30;
    public int ScreenshotMinutes { get; init; } = 5;
    public bool ScreenshotsEnabled { get; init; }
    public bool UsbFileEvents { get; init; }
    public bool AutoStart { get; init; }
    public int HotkeyFunctionKey { get; init; } = 9;

    public static MonitorSettings Load()
    {
        string path = UserFile;
        DataStore.CheckPath(path);
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new MonitorSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(path), Format);
            return (settings ?? throw new InvalidDataException("Настройки пусты.")).Validated();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Не удалось прочитать настройки из файла {path}.", ex);
        }
    }

    public void Save()
    {
        var settings = Validated();
        DataStore.CheckPath(UserFile);
        Directory.CreateDirectory(DefaultDirectory);
        string temporary = Path.Combine(DefaultDirectory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings, Format);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, UserFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private MonitorSettings Validated()
    {
        if (SampleSeconds is < 5 or > 300 || ScreenshotMinutes is < 1 or > 240 ||
            HotkeyFunctionKey is < 1 or > 12)
            throw new InvalidDataException("Проверьте интервалы и номер функциональной клавиши F1–F12.");
        string path = string.IsNullOrWhiteSpace(DataDirectory) ? DefaultDirectory :
            Environment.ExpandEnvironmentVariables(DataDirectory.Trim());
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Укажите полный путь к папке данных.");
        return this with { DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) };
    }
}

public sealed class DataStore : IDisposable
{
    private const long Quota = 1_000_000_000L;
    private const long SegmentBytes = 4L << 20;
    private const string Marker = ".pcmonitor-store-v1";
    private const string Signature = "PC Monitor Tool data directory v1";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly ConcurrentDictionary<string, State> Stores = new(Paths);
    private static readonly Regex RotatedLog = new(
        @"^(apps|files|clicks)\.[0-9]{8}T[0-9]{13}Z-[0-9a-f]{32}\.log$", RegexOptions.CultureInvariant);
    private static readonly Regex Screenshot = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?:[_.-][0-9]{1,7})?_mon[0-9]+(?:[-_][0-9]+)?\.png$",
        RegexOptions.CultureInvariant);
    private static readonly Regex PartialImage = new(@"^\.pcmonitor-png-[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant);

    private sealed record Item(string Path, long Length, DateTime LastWrite);
    private sealed class State
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly Dictionary<string, Item> Items = new(Paths);
        internal long Used;
        internal FileStream? Lease;
        internal int Users;
    }

    private readonly State state;
    private bool disposed;
    public string Root { get; }
    private string Images => Path.Combine(Root, "Screenshots");

    public DataStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Укажите полный путь к папке данных.", nameof(directory));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Paths.Equals(Root, Path.GetPathRoot(Root))) throw new IOException("Выберите отдельную папку, а не корень диска.");
        state = Stores.GetOrAdd(Root, _ => new State());
        state.Gate.Wait();
        try
        {
            CheckPath(Root);
            Directory.CreateDirectory(Root);
            string marker = Path.Combine(Root, Marker);
            CheckPath(marker);
            if (state.Lease is null)
            {
                bool existing = File.Exists(marker);
                if (!existing)
                {
                    // Папка уже могла получить настройки при первом запуске.
                    string defaultRoot = Path.GetFullPath(MonitorSettings.DefaultDirectory);
                    if (Directory.EnumerateFileSystemEntries(Root).Any(path =>
                        !(Paths.Equals(Root, defaultRoot) && Paths.Equals(Path.GetFileName(path), "settings.json"))))
                        throw new IOException("Выберите пустую папку или существующее хранилище PC Monitor Tool.");
                }
                // Блокировка файла действует также между интерактивными сеансами Windows.
                // Внутри процесса экземпляры одного хранилища используют общий дескриптор.
                state.Lease = new FileStream(marker, existing ? FileMode.Open : FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
                byte[] signature = Encoding.UTF8.GetBytes(Signature);
                if (existing)
                {
                    if (state.Lease.Length != signature.Length) throw new IOException("Неизвестный формат папки данных.");
                    byte[] actual = new byte[signature.Length];
                    state.Lease.ReadExactly(actual);
                    if (!actual.AsSpan().SequenceEqual(signature)) throw new IOException("Неизвестный формат папки данных.");
                }
                else
                {
                    state.Lease.Write(signature);
                    state.Lease.Flush(flushToDisk: true);
                }
            }

            CheckPath(Images);
            Directory.CreateDirectory(Images);
            foreach (string path in Directory.EnumerateFiles(Images, ".pcmonitor-png-*.tmp"))
            {
                if (!PartialImage.IsMatch(Path.GetFileName(path))) continue;
                CheckPath(path);
                File.Delete(path);
            }
            foreach (string stream in new[] { "apps", "files", "clicks" })
            {
                string log = Path.Combine(Root, stream + ".log");
                if (!File.Exists(log)) continue;
                CheckPath(log);
                using var live = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                TrimBrokenTail(live);
            }
            Reindex();
            MakeRoom(0, null, CancellationToken.None);
            state.Users++;
        }
        catch
        {
            if (state.Users == 0)
            {
                state.Lease?.Dispose();
                state.Lease = null;
            }
            throw;
        }
        finally { state.Gate.Release(); }
    }

    public void Dispose()
    {
        state.Gate.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            if (--state.Users == 0)
            {
                state.Lease?.Dispose();
                state.Lease = null;
                state.Items.Clear();
                state.Used = 0;
            }
        }
        finally { state.Gate.Release(); }
    }

    // Проверяем также родительские пути, чтобы ротация не удаляла файлы за junction/ссылкой.
    internal static void CheckPath(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Путь содержит ссылку или точку повторной обработки: {p}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string StreamName(string stream) => stream switch
    {
        "apps" or "apps.log" => "apps",
        "clicks" or "clicks.log" => "clicks",
        "files" or "files.log" => "files",
        _ => throw new ArgumentException("Неизвестный журнал.", nameof(stream))
    };

    private void Reindex()
    {
        CheckPath(Root);
        CheckPath(Images);
        state.Items.Clear();
        state.Used = 0;
        foreach (string path in Directory.EnumerateFiles(Root, "*.log"))
        {
            string name = Path.GetFileName(path);
            if (name is "apps.log" or "clicks.log" or "files.log" || RotatedLog.IsMatch(name)) Track(path);
        }
        foreach (string path in Directory.EnumerateFiles(Images, "*.png"))
            if (Screenshot.IsMatch(Path.GetFileName(path))) Track(path);
    }

    private void Forget(string path)
    {
        if (state.Items.Remove(path, out var item)) state.Used -= item.Length;
    }

    private void Track(string path)
    {
        CheckPath(path);
        Forget(path);
        var info = new FileInfo(path);
        if (!info.Exists) return;
        state.Items.Add(path, new Item(path, info.Length, info.LastWriteTimeUtc));
        state.Used += info.Length;
    }

    private void MakeRoom(long bytes, string? except, CancellationToken token)
    {
        if (bytes > Quota) throw new IOException("Один файл превышает лимит хранилища 1 ГБ (1 000 000 000 байт).");
        if (state.Used + bytes <= Quota) return;
        Reindex(); // Учитываем ручное удаление файлов перед вытеснением старых записей.
        foreach (var item in state.Items.Values.OrderBy(i => i.LastWrite).ThenBy(i => i.Path, Paths).ToArray())
        {
            if (state.Used + bytes <= Quota) return;
            if (Paths.Equals(item.Path, except)) continue;
            token.ThrowIfCancellationRequested();
            CheckPath(item.Path);
            File.Delete(item.Path);
            Forget(item.Path);
        }
        if (state.Used + bytes > Quota) throw new IOException("Не удалось освободить место в хранилище.");
    }

    public async Task LogAsync(string stream, object data, CancellationToken token = default)
    {
        string name = StreamName(stream);
        string path = Path.Combine(Root, name + ".log");
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckPath(path);
            byte[] record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, data }) + "\n");
            if (record.LongLength > Quota) throw new IOException("Запись превышает лимит хранилища.");
            Track(path);
            if (state.Items.TryGetValue(path, out var live) && live.Length > 0 && live.Length + record.Length > SegmentBytes)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);
                string archive = Path.Combine(Root, $"{name}.{stamp}-{Guid.NewGuid():N}.log");
                File.Move(path, archive);
                Forget(path);
                Track(archive);
            }
            MakeRoom(record.LongLength, path, token);
            try
            {
                await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.Read, 4096, FileOptions.Asynchronous);
                long original = TrimBrokenTail(file);
                try
                {
                    file.Position = original;
                    await file.WriteAsync(record, token).ConfigureAwait(false);
                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                catch { file.SetLength(original); throw; }
            }
            finally { Track(path); }
        }
        finally { state.Gate.Release(); }
    }

    private static long TrimBrokenTail(FileStream file)
    {
        // При аварийном завершении отбрасываем только незавершённую строку JSONL.
        long end = file.Length;
        if (end == 0) return 0;
        file.Position = end - 1;
        if (file.ReadByte() == '\n') return end;
        byte[] block = new byte[4096];
        while (end > 0)
        {
            int length = (int)Math.Min(end, block.Length);
            end -= length;
            file.Position = end;
            file.ReadExactly(block, 0, length);
            int index = Array.LastIndexOf(block, (byte)'\n', length - 1, length);
            if (index >= 0) { end += index + 1; break; }
        }
        file.SetLength(end);
        return end;
    }

    public async Task SavePngAsync(string fileName, byte[] bytes, CancellationToken token = default)
    {
        if (!Screenshot.IsMatch(fileName) || fileName != Path.GetFileName(fileName))
            throw new ArgumentException("Недопустимое имя снимка.", nameof(fileName));
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckPath(Images);
            MakeRoom(bytes.LongLength, null, token);
            string destination = Path.Combine(Images, fileName);
            int suffix = 1;
            while (File.Exists(destination))
                destination = Path.Combine(Images, $"{Path.GetFileNameWithoutExtension(fileName)}-{++suffix}.png");
            string temporary = Path.Combine(Images, $".pcmonitor-png-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await file.WriteAsync(bytes, token).ConfigureAwait(false);
                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination);
                Track(destination);
            }
            finally { File.Delete(temporary); }
        }
        finally { state.Gate.Release(); }
    }

    public async Task<string[]> ListScreenshotsAsync(CancellationToken token = default)
    {
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CheckPath(Images);
            // Перечисление большого архива не должно задерживать переключение вкладок.
            return await Task.Run(() => state.Items.Values.Where(i => i.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(i.Path)).OrderByDescending(i => i.LastWrite).ThenByDescending(i => i.Path, Paths)
                .Select(i => { token.ThrowIfCancellationRequested(); CheckPath(i.Path); return i.Path; }).ToArray(),
                token).ConfigureAwait(false);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<string[]> ReadRecentLinesAsync(string stream, int count, CancellationToken token = default)
    {
        string name = StreamName(stream);
        if (count <= 0) return Array.Empty<string>();
        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var rows = new List<string>();
            var files = state.Items.Values.Where(i => Path.GetFileName(i.Path).StartsWith(name + ".", StringComparison.Ordinal))
                .OrderByDescending(i => Paths.Equals(Path.GetFileName(i.Path), name + ".log"))
                .ThenByDescending(i => Path.GetFileName(i.Path), StringComparer.Ordinal).ToArray();
            foreach (var item in files)
            {
                if (!File.Exists(item.Path)) continue;
                CheckPath(item.Path);
                var tail = new Queue<string>();
                int remaining = count - rows.Count;
                await foreach (string line in File.ReadLinesAsync(item.Path, token).ConfigureAwait(false))
                {
                    if (tail.Count == remaining) tail.Dequeue();
                    tail.Enqueue(line);
                }
                rows.AddRange(tail.Reverse());
                if (rows.Count == count) break;
            }
            return rows.ToArray();
        }
        finally { state.Gate.Release(); }
    }

    public async Task ExportAsync(string destination, CancellationToken token = default)
    {
        string zip = Path.GetFullPath(destination);
        CheckPath(zip);
        string prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (zip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Paths.Equals(Root, zip) ||
            !zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выберите ZIP-файл за пределами папки данных.");
        string temporary = Path.Combine(Path.GetDirectoryName(zip)!, $".pcmonitor-export-{Guid.NewGuid():N}.tmp");

        await state.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Reindex();
            // В ZIP попадают только собранные события и снимки. Папка может содержать личные файлы пользователя.
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var item in state.Items.Values.OrderBy(i => i.Path, Paths))
                {
                    token.ThrowIfCancellationRequested();
                    CheckPath(item.Path);
                    string relative = Path.GetRelativePath(Root, item.Path).Replace(Path.DirectorySeparatorChar, '/');
                    var level = relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ?
                        CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    var entry = archive.CreateEntry(relative, level);
                    using var output = entry.Open();
                    await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, zip, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { state.Gate.Release(); }
        }
    }
}
