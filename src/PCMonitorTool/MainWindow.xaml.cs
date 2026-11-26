using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCMonitorTool;

public partial class MainWindow : Window
{
    private const int LogLimit = 250;
    private const int ReportLimit = 5000;
    private readonly DispatcherTimer _reportTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DataStore? _store;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _preview;
    private bool _active;

    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? SettingsRequested;
    public event Action? CaptureRequested;
    public event Action? ExportRequested;
    public event Action? OpenFolderRequested;
    public event Action? ExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _reportTimer.Tick += (_, _) => { if (_refresh is null && WorkspaceTabs.SelectedIndex == 0) RefreshView(); };
        IsVisibleChanged += (_, _) =>
        {
            UpdateReportTimer();
            if (IsVisible) RefreshView();
        };
        StateChanged += (_, _) => UpdateReportTimer();
        Closed += (_, _) => { _reportTimer.Stop(); _refresh?.Cancel(); _preview?.Cancel(); };
    }

    public void SetStore(DataStore store)
    {
        _store = store;
        PreviewImage.Source = null;
        RefreshView();
    }

    public void SetStatus(bool active, bool automatic, string detail)
    {
        _active = active;
        UpdateReportTimer();
        CaptureButton.IsEnabled = active;
        CaptureButton.ToolTip = active ? "После подтверждения будет сохранён снимок каждого экрана." : "Чтобы сделать снимок, начните сеанс учёта.";
        SessionButton.Content = active ? "Остановить сеанс" : "Начать сеанс";
        StatusText.Text = active ? "Идёт мониторинг" : "Сеанс остановлен";
        StatusDot.Fill = active ? Brushes.LightGreen : Brushes.LightSlateGray;
        ModeText.Text = active && automatic ? "Автоматические снимки включены" : "Автоматические снимки выключены";
        DetailText.Text = detail;
        ClickHint.Text = active ? "Нажмите здесь для записи тестового клика" : "Для проверки начните сеанс";
        RefreshView();
    }

    private void UpdateReportTimer()
    {
        _reportTimer.IsEnabled = _active && IsVisible && WindowState != WindowState.Minimized && WorkspaceTabs.SelectedIndex == 0;
    }

    public void ShowDashboard()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_active) StopRequested?.Invoke();
        else StartRequested?.Invoke();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshView();

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // События дочерних таблиц и списков тоже всплывают до TabControl.
        if (e.OriginalSource != WorkspaceTabs || !IsLoaded) return;
        UpdateReportTimer();
        RefreshView();
    }

    public async void RefreshView()
    {
        _refresh?.Cancel();
        _preview?.Cancel();
        var store = _store;
        if (store is null || !IsVisible) return;
        using var source = new CancellationTokenSource();
        _refresh = source;
        var token = source.Token;
        try
        {
            int tab = WorkspaceTabs.SelectedIndex;
            FeedbackText.Text = "";
            if (tab == 0)
            {
                var lines = await store.ReadRecentLinesAsync("apps", ReportLimit + 1, token);
                var report = await Task.Run(() => BuildReport(lines, token), token);
                token.ThrowIfCancellationRequested();
                UsageGrid.ItemsSource = report.Rows;
                UsageHint.Text = report.Limited
                    ? "Часть журнала осталась за пределами выборки. Показано время по последним 5 000 событиям."
                    : "Сумма интервалов активной работы с полуночи по местному времени.";
                TotalText.Text = report.Rows.Length == 0 ? "Сегодня ещё нет учтённых интервалов" : $"Всего за показанные интервалы: {FormatSeconds(report.Rows.Sum(x => x.Seconds))}";
            }
            else if (tab == 1)
            {
                string? current = (ScreenshotList.SelectedItem as PictureFile)?.Path;
                var paths = await store.ListScreenshotsAsync(token);
                token.ThrowIfCancellationRequested();
                var files = paths.Take(LogLimit).Select(p => new PictureFile(p, Path.GetFileName(p))).ToArray();
                ScreenshotList.ItemsSource = files;
                ScreenshotList.SelectedItem = files.FirstOrDefault(p => p.Path == current) ?? files.FirstOrDefault();
                if (files.Length == 0) ClearPreview("Сохранённых снимков пока нет");
                FeedbackText.Text = paths.Length > files.Length ? $"Показаны последние {files.Length} снимков" : $"Снимков: {files.Length}";
            }
            else if (tab >= 2 && tab <= 4)
            {
                var (stream, grid) = tab switch
                {
                    2 => ("clicks", ClickGrid),
                    3 => ("files", FileGrid),
                    _ => ("apps", AppGrid)
                };
                var lines = await store.ReadRecentLinesAsync(stream, LogLimit, token);
                var rows = await Task.Run(() => lines.Select(ToEventRow).ToArray(), token);
                token.ThrowIfCancellationRequested();
                grid.ItemsSource = rows;
                FeedbackText.Text = rows.Length switch
                {
                    0 => "Журнал пока пуст",
                    LogLimit => $"Последние {LogLimit} событий",
                    _ => $"Событий: {rows.Length}"
                };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) FeedbackText.Text = $"Не удалось прочитать данные: {ex.Message}"; }
        finally { if (_refresh == source) _refresh = null; }
    }

    private async void ClickSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var store = _store;
        if (!_active || store is null) return;
        var point = e.GetPosition(ClickSurface);
        e.Handled = true;
        try
        {
            // Координаты относятся только к явно обозначенной тестовой площадке, в единицах WPF.
            await store.LogAsync("clicks", new { kind = "test.click", button = e.ChangedButton.ToString(), x = (int)point.X, y = (int)point.Y });
            if (_store == store && WorkspaceTabs.SelectedIndex == 2) RefreshView();
        }
        catch (Exception ex) { FeedbackText.Text = $"Не удалось записать клик: {ex.Message}"; }
    }

    private async void ScreenshotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _preview?.Cancel();
        if (ScreenshotList.SelectedItem is not PictureFile picture) { ClearPreview("Выберите снимок"); return; }
        using var source = new CancellationTokenSource();
        _preview = source;
        var token = source.Token;
        ClearPreview("Загрузка снимка…");
        try
        {
            var bitmap = await Task.Run(() =>
            {
                // OnLoad позволяет сразу освободить файл, чтобы ротация могла его удалить.
                using var input = new FileStream(picture.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var result = new BitmapImage();
                result.BeginInit();
                result.CacheOption = BitmapCacheOption.OnLoad;
                result.DecodePixelWidth = 960;
                result.StreamSource = input;
                result.EndInit();
                result.Freeze();
                return result;
            }, token);
            token.ThrowIfCancellationRequested();
            PreviewImage.Source = bitmap;
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ClearPreview($"Не удалось открыть снимок. {ex.Message}"); }
        finally { if (_preview == source) _preview = null; }
    }

    private void ClearPreview(string message)
    {
        PreviewImage.Source = null;
        PreviewHint.Text = message;
        PreviewHint.Visibility = Visibility.Visible;
    }

    public sealed record UsageRow(string Process, long Seconds) { public string Duration => FormatSeconds(Seconds); }
    public sealed record EventRow(string Time, string Kind, string Detail);
    public sealed record PictureFile(string Path, string Name);

    private static (UsageRow[] Rows, bool Limited) BuildReport(string[] lines, CancellationToken token)
    {
        DateTimeOffset dayStart = new(DateTime.Today);
        DateTimeOffset dayEnd = new(DateTime.Today.AddDays(1));
        var grouped = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool olderRowsMayBeToday = lines.Length > ReportLimit;
        for (int i = 0; i < Math.Min(lines.Length, ReportLimit); i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                if (ReadInstant(root, "timestamp") is { } timestamp && timestamp < dayStart) olderRowsMayBeToday = false;
                var data = root.GetProperty("data");
                if (Text(data, "kind") != "foreground" || ReadInstant(data, "startedUtc") is not { } start || ReadInstant(data, "endedUtc") is not { } end) continue;
                if (end <= start || end <= dayStart || start >= dayEnd || !data.TryGetProperty("seconds", out var secondsValue) || !secondsValue.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds <= 0) continue;
                // Пересечение с календарными сутками учитывает также интервалы через полночь.
                var inside = (end < dayEnd ? end : dayEnd) - (start > dayStart ? start : dayStart);
                seconds = Math.Min(seconds, (end - start).TotalSeconds) * (inside.TotalSeconds / (end - start).TotalSeconds);
                var process = Text(data, "process");
                if (!string.IsNullOrWhiteSpace(process)) grouped[process] = grouped.GetValueOrDefault(process) + seconds;
            }
            catch (JsonException) { }
            catch (KeyNotFoundException) { }
            catch (InvalidOperationException) { }
        }
        return (grouped.Select(p => new UsageRow(p.Key, (long)Math.Round(p.Value))).OrderByDescending(p => p.Seconds).ThenBy(p => p.Process).ToArray(), olderRowsMayBeToday);
    }

    private static EventRow ToEventRow(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var data = root.GetProperty("data");
            var time = (ReadInstant(data, "observedUtc") ?? ReadInstant(data, "endedUtc") ?? ReadInstant(root, "timestamp"))?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
            var kind = Text(data, "kind");
            var label = kind switch
            {
                "foreground" => "Активное приложение", "process_start" => "Запуск", "process_stop" => "Завершение",
                "volume.present" => "Уже подключён", "volume.arrived" => "Подключение", "volume.removed" => "Отключение",
                "file.created" => "Создан файл", "file.changed" => "Изменение", "file.deleted" => "Удаление", "file.renamed" => "Переименование",
                "watcher.gap" => "Пропуск наблюдения", "events_dropped" => "Пропуск событий", "test.click" => ButtonLabel(Text(data, "button")),
                _ => string.IsNullOrEmpty(kind) ? "Запись" : kind
            };
            string detail = kind switch
            {
                "foreground" => $"{Text(data, "process")} · {DurationValue(data)}",
                "process_start" or "process_stop" => $"{Text(data, "process")} · PID {Text(data, "pid")}",
                "test.click" => $"X = {Text(data, "x")}, Y = {Text(data, "y")}",
                "events_dropped" => $"Количество: {Text(data, "count")}",
                _ => FileDetail(data)
            };
            return new EventRow(time, label, detail);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new EventRow("—", "Ошибка формата", "Не удалось прочитать строку журнала");
        }
    }

    private static string ButtonLabel(string name) => name switch
    {
        "Left" => "Левая кнопка", "Right" => "Правая кнопка", "Middle" => "Средняя кнопка",
        "XButton1" => "Доп. кнопка 1", "XButton2" => "Доп. кнопка 2", _ => "Тестовый клик"
    };

    private static string FileDetail(JsonElement data)
    {
        var parts = new List<string>();
        foreach (string key in new[] { "drive", "oldName", "name" })
        {
            var part = Text(data, key);
            if (part.Length > 0) parts.Add(part);
        }
        if (Text(data, "sizeBytes") is { Length: > 0 } size) parts.Add($"{size} Б");
        return string.Join(" · ", parts);
    }

    private static string DurationValue(JsonElement data) =>
        data.TryGetProperty("seconds", out var value) && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue
            ? FormatSeconds((long)Math.Round(seconds)) : "—";

    private static string FormatSeconds(long seconds) => seconds >= 3600 ? $"{seconds / 3600} ч {seconds / 60 % 60} мин" : seconds >= 60 ? $"{seconds / 60} мин {seconds % 60} с" : $"{seconds} с";

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" };
    }

    private static DateTimeOffset? ReadInstant(JsonElement element, string name) => DateTimeOffset.TryParse(Text(element, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}
