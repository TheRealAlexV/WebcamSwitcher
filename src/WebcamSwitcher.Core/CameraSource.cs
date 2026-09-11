using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

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
    private int _nullFrames;
    private int _firstFrameLogged;
    private int _convertErrors;

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

    public async Task<bool> StartAsync(int outWidth, int outHeight, int captureWidth = 0, int captureHeight = 0,
        IReadOnlyList<MediaFrameSourceGroup>? groups = null)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        _nv12 = new byte[Protocol.Nv12Size(outWidth, outHeight)];
        _preview = new byte[PreviewWidth * PreviewHeight * 4];

        AppLog.Write($"Source.StartAsync({DisplayName}): finding group");
        // Callers may pass a pre-enumerated list so N cameras don't each perform
        // a full redundant device scan; when null, enumerate as before.
        groups ??= await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g => g.Id == DeviceId);
        if (group == null)
        {
            AppLog.Write($"Source.StartAsync({DisplayName}): group NOT FOUND");
            return false;
        }

        _capture = new MediaCapture();
        AppLog.Write($"Source.StartAsync({DisplayName}): InitializeAsync");
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });

        var colorSource = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (colorSource == null)
            return false;

        // Diagnostic: surface the native + supported formats so aspect/padding issues are visible.
        try
        {
            var fmts = colorSource.SupportedFormats
                .Select(f => $"{f.VideoFormat.Width}x{f.VideoFormat.Height}")
                .Distinct()
                .ToList();
            var cur = colorSource.CurrentFormat?.VideoFormat;
            AppLog.Write($"Source({DisplayName}): native={cur?.Width}x{cur?.Height} supported=[{string.Join(",", fmts)}]");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): format probe failed: {ex.Message}");
        }

        // Match the camera's capture format to the output aspect. The C920, for
        // example, defaults to 640x480 (4:3); scaling that into a 16:9 output
        // pillarboxes it. Selecting a native 16:9 mode fills the frame and lets
        // the Frame Server skip the resize entirely.
        await TrySetCaptureFormatAsync(colorSource, captureWidth, captureHeight);

        AppLog.Write($"Source.StartAsync({DisplayName}): CreateFrameReaderAsync");
        _reader = await StartReaderAsync(colorSource);
        // Realtime = drop frames rather than queue when the handler falls behind
        // (the default, set explicitly for low latency / no backlog).
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        AppLog.Write($"Source.StartAsync({DisplayName}): reader started");
        Running = true;
        return true;
    }

    /// <summary>
    /// Chooses and applies a capture format whose aspect matches the output, so the
    /// Frame Server does not pillarbox/letterbox. When an explicit capture size is
    /// configured, the closest supported format to that size is used instead.
    /// </summary>
    private async Task TrySetCaptureFormatAsync(MediaFrameSource source, int captureWidth, int captureHeight)
    {
        try
        {
            var formats = source.SupportedFormats;
            if (formats == null || formats.Count == 0)
                return;

            int wantW = captureWidth > 0 ? captureWidth : OutputWidth;
            int wantH = captureHeight > 0 ? captureHeight : OutputHeight;
            bool forced = captureWidth > 0 && captureHeight > 0;
            double targetAspect = (double)wantW / wantH;
            double targetArea = (double)wantW * wantH;

            MediaFrameFormat best = formats.OrderBy(f =>
            {
                double area = (double)f.VideoFormat.Width * f.VideoFormat.Height;
                if (forced)
                    return (Math.Abs(area - targetArea), 0.0, 0.0);

                bool exact = f.VideoFormat.Width == wantW && f.VideoFormat.Height == wantH;
                double aspectErr = Math.Abs((double)f.VideoFormat.Width / f.VideoFormat.Height - targetAspect);
                return (exact ? 0.0 : 1.0, Math.Round(aspectErr, 4), Math.Abs(area - targetArea));
            }).First();

            var cur = source.CurrentFormat;
            if (cur != null && cur.VideoFormat.Width == best.VideoFormat.Width && cur.VideoFormat.Height == best.VideoFormat.Height)
            {
                AppLog.Write($"Source({DisplayName}): capture format already {best.VideoFormat.Width}x{best.VideoFormat.Height}");
                return;
            }

            await source.SetFormatAsync(best);
            AppLog.Write($"Source({DisplayName}): capture format -> {best.VideoFormat.Width}x{best.VideoFormat.Height} (target {wantW}x{wantH}{(forced ? ", forced" : "")})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): SetFormat failed ({ex.Message}); keeping default");
        }
    }

    /// <summary>
    /// Creates and starts a frame reader that produces a decoded color frame.
    /// Requesting Bgra8 (rather than leaving the subtype unset) guarantees
    /// <see cref="VideoMediaFrame.SoftwareBitmap"/> is non-null for cameras whose
    /// native format is compressed (MJPG/H.264).
    /// </summary>
    private async Task<MediaFrameReader> StartReaderAsync(MediaFrameSource colorSource)
    {
        // Request NV12 at the exact output size so the Frame Server does decode +
        // resize (and color conversion) itself, removing the managed BGRA→NV12
        // conversion from the hot path.
        try
        {
            var r = await _capture!.CreateFrameReaderAsync(
                colorSource, MediaEncodingSubtypes.Nv12,
                new BitmapSize { Width = (uint)OutputWidth, Height = (uint)OutputHeight });
            await r.StartAsync();
            AppLog.Write($"Source({DisplayName}): frame reader = Nv12 {OutputWidth}x{OutputHeight}");
            return r;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): Nv12 reader failed ({ex.Message}); falling back to Bgra8");
        }

        var bgra = await _capture!.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
        await bgra.StartAsync();
        AppLog.Write($"Source({DisplayName}): frame reader = Bgra8");
        return bgra;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var sw = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sw == null)
        {
            if ((Interlocked.Increment(ref _nullFrames) & 0x3F) == 1)
                AppLog.Write($"Source({DisplayName}): null SoftwareBitmap x{_nullFrames}");
            return;
        }
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            AppLog.Write($"Source({DisplayName}): first frame {sw.PixelWidth}x{sw.PixelHeight} {sw.BitmapPixelFormat}");

        try
        {
            ConvertToNv12(sw);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _convertErrors) <= 5)
                AppLog.Write($"Source({DisplayName}): ConvertToNv12 EX #{_convertErrors} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private unsafe void ConvertToNv12(SoftwareBitmap sw)
    {
        using var buffer = sw.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var byteAccess = reference.As<IMemoryBufferByteAccess>();
        byteAccess.GetBuffer(out byte* data, out uint _);

        byte* src = data + plane.StartIndex;
        int w = (int)plane.Width;
        int h = (int)plane.Height;
        int stride = (int)plane.Stride;

        byte[] nv12;
        byte[] preview;
        lock (_lock)
        {
            nv12 = _nv12;
            preview = _preview;
        }

        if (sw.BitmapPixelFormat == BitmapPixelFormat.Nv12)
        {
            // NV12 in → tight NV12 out (stride-aware copy). The Frame Server
            // already decoded/resized, so no color conversion on the hot path.
            // Published for EVERY camera: each source continuously drives its own
            // passthrough virtual camera, not just the active one.
            if (w == OutputWidth && h == OutputHeight)
            {
                CopyNv12Tight(src, w, h, stride, nv12);
                FrameReady?.Invoke(nv12);
            }
            ColorConverter.Nv12ToBgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }
        else
        {
            // Fallback: reader delivered Bgra8 (rare). Legacy conversion path.
            ColorConverter.Bgra8ToNv12(src, w, h, stride, nv12, OutputWidth, OutputHeight);
            FrameReady?.Invoke(nv12);
            ColorConverter.Bgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }

        PreviewReady?.Invoke(preview, PreviewWidth, PreviewHeight);
    }

    private static unsafe void CopyNv12Tight(byte* src, int w, int h, int srcStride, byte[] dst)
    {
        fixed (byte* d = dst)
        {
            if (srcStride == w)
            {
                // Tight source: Y and UV planes are contiguous, so one copy covers all.
                int total = w * h + w * (h / 2);
                if (total <= dst.Length)
                    Buffer.MemoryCopy(src, d, dst.Length, total);
                return;
            }

            for (int y = 0; y < h; y++)
                Buffer.MemoryCopy(src + y * srcStride, d + y * w, w, w);

            byte* uv = src + srcStride * h;
            byte* duv = d + w * h;
            for (int y = 0; y < h / 2; y++)
                Buffer.MemoryCopy(uv + y * srcStride, duv + y * w, w, w);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                AppLog.Write($"Source.Dispose({DisplayName}): StopAsync");
                await _reader.StopAsync();
                AppLog.Write($"Source.Dispose({DisplayName}): StopAsync done");
            }
        }
        catch { }

        _reader = null;
        AppLog.Write($"Source.Dispose({DisplayName}): MediaCapture.Dispose");
        _capture?.Dispose();
        _capture = null;
        Running = false;
    }
}
