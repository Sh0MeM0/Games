using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PCMonitorTool;

/// <summary>Один источник, явно предоставленный пользователем в системном диалоге Windows.</summary>
public sealed class SharedScreenCapture : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int Buffers = 2;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly Action _onClosed;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private SizeInt32 _size;
    private bool _reading;
    private bool _disposed;
    private bool _sessionReleased;
    private bool _sourceLost;

    private SharedScreenCapture(GraphicsCaptureItem item, IDirect3DDevice device,
        Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, SizeInt32 size,
        Dispatcher dispatcher, Action onClosed)
    {
        _item = item;
        _device = device;
        _pool = pool;
        _session = session;
        _dispatcher = dispatcher;
        _onClosed = onClosed;
        _size = size;
    }

    public static async Task<SharedScreenCapture?> PickAsync(Window owner, Action onSourceClosed, CancellationToken token)
    {
        owner.Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            throw new PlatformNotSupportedException("Для системного выбора экрана нужна Windows 10 1903 или новее. Доступен ручной снимок.");

        IDirect3DDevice? device = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        SharedScreenCapture? source = null;
        bool started = false;
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Windows Graphics Capture недоступен.");
            var picker = new GraphicsCapturePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, new WindowInteropHelper(owner).EnsureHandle());
            // При каждом запуске Windows заново предлагает выбрать окно или экран.
            GraphicsCaptureItem? item = await picker.PickSingleItemAsync().AsTask(token);
            token.ThrowIfCancellationRequested();
            if (item is null) return null;
            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException("Окно или экран сейчас недоступны.");

            device = CreateDevice();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, PixelFormat, Buffers, size);
            session = pool.CreateCaptureSession(item);
            // В старых версиях Windows системная рамка обязательна. В новых явно требуем её.
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
#pragma warning disable CA1416 // Доступность свойства проверена по метаданным WinRT.
                session.IsBorderRequired = true;
                if (!session.IsBorderRequired)
                    throw new InvalidOperationException("Системная рамка захвата недоступна.");
#pragma warning restore CA1416
            }
            source = new SharedScreenCapture(item, device, pool, session, size, owner.Dispatcher, onSourceClosed);
            item.Closed += source.ItemClosed;
            token.ThrowIfCancellationRequested();
            session.StartCapture();
            started = true;
            return source;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x800704C7) && !token.IsCancellationRequested)
        {
            return null; // Отмена пользователем в системном диалоге.
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Windows не смогла начать захват выбранного источника. Доступен ручной снимок. " + ex.Message, ex);
        }
        finally
        {
            // Успешно запущенным сеансом и всеми его ресурсами владеет возвращаемый объект.
            if (source is null)
            {
                session?.Dispose();
                pool?.Dispose();
                device?.Dispose();
            }
            else if (!started) source.Dispose();
        }
    }

    public async Task<byte[]?> GetPngAsync(CancellationToken token)
    {
        CancellationTokenSource linked;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || _sourceLost || _reading) return null;
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            _reading = true;
        }
        using (linked)
        {
            try
            {
                var cancel = linked.Token;
                long start = Stopwatch.GetTimestamp();
                // SystemRelativeTime у WGC и Stopwatch в Windows используют один источник QPC.
                var earliest = Stopwatch.GetElapsedTime(0, start) - TimeSpan.FromMilliseconds(150);
                while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
                {
                    cancel.ThrowIfCancellationRequested();
                    Direct3D11CaptureFrame? frame = _pool.TryGetNextFrame();
                    if (frame is null)
                    {
                        await Task.Delay(40, cancel).ConfigureAwait(false);
                        continue;
                    }

                    bool resize;
                    var content = frame.ContentSize;
                    using (frame)
                    {
                        if (content.Width <= 0 || content.Height <= 0) return null;
                        resize = content.Width != _size.Width || content.Height != _size.Height;
                        if (!resize && frame.SystemRelativeTime >= earliest)
                        {
                            byte[] bytes = await EncodeAsync(frame, content, cancel).ConfigureAwait(false);
                            cancel.ThrowIfCancellationRequested();
                            lock (_sync) return _sourceLost || _disposed ? null : bytes;
                        }
                    }
                    if (resize)
                    {
                        // Перед Recreate кадр возвращён в пул; содержимое старого размера отбрасываем.
                        _pool.Recreate(_device, PixelFormat, Buffers, content);
                        _size = content;
                    }
                }
                // Свежего кадра нет, например окно свёрнуто: не датируем старое изображение новым временем.
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (linked.IsCancellationRequested)
            {
                throw new OperationCanceledException(linked.Token);
            }
            catch
            {
                SourceLost();
                throw;
            }
            finally
            {
                bool release;
                lock (_sync)
                {
                    _reading = false;
                    release = _sessionReleased;
                }
                if (release) ReleaseBuffers();
            }
        }
    }

    private static async Task<byte[]> EncodeAsync(Direct3D11CaptureFrame frame, SizeInt32 content, CancellationToken token)
    {
        // Копирование GPU → CPU и кодирование нужны лишь при сохранении очередного снимка.
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask(token).ConfigureAwait(false);
        if (bitmap.PixelWidth < content.Width || bitmap.PixelHeight < content.Height)
            throw new InvalidDataException("Размер поверхности источника изменился.");
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(token).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        encoder.BitmapTransform.Bounds = new BitmapBounds { Width = (uint)content.Width, Height = (uint)content.Height };
        await encoder.FlushAsync().AsTask(token).ConfigureAwait(false);
        stream.Seek(0);
        byte[] bytes = new byte[checked((int)stream.Size)];
        using var input = stream.AsStreamForRead();
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args) => SourceLost();

    private void SourceLost()
    {
        lock (_sync)
        {
            if (_disposed || _sourceLost) return;
            _sourceLost = true;
        }
        // Не закрываем WinRT-сеанс непосредственно из его обратного вызова.
        _ = Task.Run(() =>
        {
            try { Dispose(); }
            catch (COMException) { /* Windows могла уже освободить отозванный источник. */ }
            finally
            {
                if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(_onClosed);
            }
        });
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Рамка исчезает при завершении самого системного сеанса даже во время обработки PNG.
        _lifetime.Cancel();
        try { _item.Closed -= ItemClosed; }
        finally
        {
            try { _session.Dispose(); }
            finally
            {
                bool release;
                lock (_sync)
                {
                    _sessionReleased = true;
                    release = !_reading;
                }
                if (release) ReleaseBuffers();
            }
        }
    }

    private void ReleaseBuffers()
    {
        try { _pool.Dispose(); }
        finally
        {
            try { _device.Dispose(); }
            finally { _lifetime.Dispose(); }
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        IntPtr d3d = IntPtr.Zero, dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            // Аппаратный D3D11; WARP подходит для систем без поддерживаемого адаптера.
            int result = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d3d, IntPtr.Zero, IntPtr.Zero);
            if (result < 0)
            {
                if (d3d != IntPtr.Zero) Marshal.Release(d3d);
                Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20,
                    IntPtr.Zero, 0, 7, out d3d, IntPtr.Zero, IntPtr.Zero));
            }
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (d3d != IntPtr.Zero) Marshal.Release(d3d);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint count, uint sdkVersion, out IntPtr device, IntPtr selectedLevel, IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr inspectable);
}
