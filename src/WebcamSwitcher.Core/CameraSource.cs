using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace WebcamSwitcher.Core;

// COM interface used to read the raw bytes of a SoftwareBitmap's buffer.
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

/// <summary>
/// Captures a single physical camera in non-blocking share mode via
/// MediaFrameReader and normalizes each frame to NV12 at the output size.
/// </summary>
public sealed class CameraSource : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private byte[] _nv12 = Array.Empty<byte>();
    private byte[] _preview = Array.Empty<byte>();
    private readonly object _lock = new();

    public const int PreviewWidth = 320;
    public const int PreviewHeight = 180;

    public string DeviceId { get; }
    public string DisplayName { get; }
    public bool Running { get; private set; }

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>Raised with the latest normalized NV12 frame (buffer is reused).</summary>
    public event Action<byte[]>? FrameReady;

    /// <summary>Raised with the latest downscaled Bgra8 preview (buffer is reused).</summary>
    public event Action<byte[], int, int>? PreviewReady;

    public CameraSource(string deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public async Task<bool> StartAsync(int outWidth, int outHeight)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        _nv12 = new byte[Protocol.Nv12Size(outWidth, outHeight)];
        _preview = new byte[PreviewWidth * PreviewHeight * 4];

        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g => g.Id == DeviceId);
        if (group == null)
            return false;

        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });

        var colorSource = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (colorSource == null)
            return false;

        _reader = await _capture.CreateFrameReaderAsync(colorSource);
        _reader.FrameArrived += OnFrameArrived;
        await _reader.StartAsync();
        Running = true;
        return true;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var sw = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sw == null)
            return;

        SoftwareBitmap? converted = null;
        if (sw.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            converted = SoftwareBitmap.Convert(sw, BitmapPixelFormat.Bgra8);
            if (converted == null)
                return;
            sw = converted;
        }

        try
        {
            ConvertToNv12(sw);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private unsafe void ConvertToNv12(SoftwareBitmap sw)
    {
        using var buffer = sw.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        ((IMemoryBufferByteAccess)reference).GetBuffer(out byte* data, out uint _);

        byte* src = data + plane.StartIndex;

        byte[] nv12;
        byte[] preview;
        lock (_lock)
        {
            nv12 = _nv12;
            preview = _preview;
        }

        ColorConverter.Bgra8ToNv12(src, plane.Width, plane.Height, (int)plane.Stride, nv12, OutputWidth, OutputHeight);
        FrameReady?.Invoke(nv12);

        ColorConverter.Bgra8Downscale(src, plane.Width, plane.Height, (int)plane.Stride, preview, PreviewWidth, PreviewHeight);
        PreviewReady?.Invoke(preview, PreviewWidth, PreviewHeight);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                await _reader.StopAsync();
            }
        }
        catch { }

        _reader = null;
        _capture?.Dispose();
        _capture = null;
        Running = false;
    }
}
