using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PingWhen.Shell;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace PingWhen.Features;

/// <summary>
/// Windows.Graphics.Capture of one window (v1.0, spec §5.3): works for windows behind other windows, not for minimized ones.
/// A free-threaded frame pool with MinUpdateInterval 1 s; frames only arrive when the window's content changes.
/// The yellow border is turned off (allowed for unpackaged apps on 24H2+, checked on 26300; older builds keep it).
/// </summary>
public sealed class RegionCapture : IDisposable
{
    static IDirect3DDevice? device;
    static readonly Lock DeviceGate = new();

    readonly Direct3D11CaptureFramePool pool;
    readonly GraphicsCaptureSession session;
    readonly Action<byte[], int, int> onFrame;
    Windows.Graphics.SizeInt32 size;
    int busy;

    RegionCapture(GraphicsCaptureItem item, Action<byte[], int, int> onFrame)
    {
        this.onFrame = onFrame;
        size = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Device(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        session = pool.CreateCaptureSession(item);
        try { session.IsBorderRequired = false; } catch { }                   // not on older builds: the border stays
        try { session.MinUpdateInterval = TimeSpan.FromSeconds(1); } catch { } // 26100+; older builds drop frames by time below
        try { session.IsCursorCaptureEnabled = false; } catch { }
        pool.FrameArrived += OnFrame;
        session.StartCapture();
    }

    /// <summary>Null when the window can't be captured (gone, a shell window, Windows 10 before 22H2 without the interop path).</summary>
    public static RegionCapture? Start(long hwnd, Action<byte[], int, int> onFrame)
    {
        try
        {
            var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd));
            return item is null ? null : new RegionCapture(item, onFrame);
        }
        catch (Exception e) { Log.Warn("capture not started", e); return null; }
    }

    long lastAt;

    async void OnFrame(Direct3D11CaptureFramePool p, object _)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) { p.TryGetNextFrame()?.Dispose(); return; } // still copying the previous one
        try
        {
            using var frame = p.TryGetNextFrame();
            if (frame is null) return;
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
            {
                size = frame.ContentSize; // resized: the next frames come in the new size
                p.Recreate(Device(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            }
            var now = Environment.TickCount64;
            if (now - lastAt < 900) return; // MinUpdateInterval is not there before 26100
            lastAt = now;
            using var bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
            onFrame(Bytes(bmp), bmp.PixelWidth, bmp.PixelHeight);
        }
        catch (Exception e) { Log.Warn("capture frame", e); }
        finally { Volatile.Write(ref busy, 0); }
    }

    public void Dispose()
    {
        pool.FrameArrived -= OnFrame;
        try { session.Dispose(); pool.Dispose(); } catch { }
    }

    /// <summary>One frame of the window now (the region picker's still frame, screenshots in notifications).</summary>
    public static async Task<(byte[] Bgra, int Width, int Height)?> Snapshot(long hwnd, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<(byte[], int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c = Start(hwnd, (b, w, h) => tcs.TrySetResult((b, w, h)));
        if (c is null) return null;
        try { return await tcs.Task.WaitAsync(timeout); }
        catch (TimeoutException) { return null; }
    }

    static byte[] Bytes(SoftwareBitmap b)
    {
        var buf = new Windows.Storage.Streams.Buffer((uint)(b.PixelWidth * b.PixelHeight * 4));
        b.CopyToBuffer(buf);
        return buf.ToArray();
    }

    static IDirect3DDevice Device()
    {
        lock (DeviceGate)
        {
            if (device is not null) return device;
            const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0, 0, D3D11_SDK_VERSION, out var d3d, out _, out var context));
            try
            {
                var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgiIid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    Marshal.Release(inspectable);
                }
                finally { Marshal.Release(dxgi); }
            }
            finally { Marshal.Release(d3d); Marshal.Release(context); }
            return device;
        }
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint numLevels, uint sdk, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>The region (or the whole window) as JPEG of at most <paramref name="maxBytes"/>: scaled to ≤ 1280 px, quality lowered until it fits.</summary>
    public static byte[] Jpeg(byte[] bgra, int width, int height, Frames.Rect? region, int maxBytes = 300_000)
    {
        BitmapSource src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        if (region is { IsEmpty: false } r)
        {
            var (x, y, w, h) = r.Pixels(width, height);
            src = new CroppedBitmap(src, new Int32Rect(x, y, w, h));
        }
        var scale = Math.Min(1.0, 1280.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        for (var quality = 85; ; quality -= 15)
        {
            BitmapSource s = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
            var enc = new JpegBitmapEncoder { QualityLevel = Math.Max(20, quality) };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s));
            using var ms = new MemoryStream();
            enc.Save(ms);
            if (ms.Length <= maxBytes || (quality <= 20 && scale < 0.2)) return ms.ToArray();
            if (quality <= 20) { quality = 100; scale *= 0.7; } // smaller picture, start over at 85
        }
    }

    /// <summary>Windows OCR in the user's languages; null when no OCR language is installed.</summary>
    public static async Task<string?> Ocr(byte[] bgra, int width, int height, Frames.Rect region)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        var (x, y, w, h) = region.Pixels(width, height);
        var crop = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Buffer.BlockCopy(bgra, ((y + row) * width + x) * 4, crop, row * w * 4, w * 4);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(crop.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bmp);
        return result.Text;
    }

    public static bool OcrAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;
}
