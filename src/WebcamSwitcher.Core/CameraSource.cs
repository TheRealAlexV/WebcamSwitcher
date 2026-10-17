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
    private MfMjpegSource? _mf;
    private byte[] _nv12 = Array.Empty<byte>();
    private byte[] _preview = Array.Empty<byte>();
    private readonly object _lock = new();
    private int _nullFrames;
    private int _firstFrameLogged;
    private int _convertErrors;
    private int _framesArrived;
    private int _fpsWindowFrames;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

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

    /// <summary>
    /// When false, the per-frame preview downscale is skipped entirely (no CPU cost,
    /// no <see cref="PreviewReady"/>). Capture and virtual-camera publishing are
    /// unaffected.
    /// </summary>
    public bool PreviewEnabled { get; set; } = true;

    /// <summary>
    /// Capture-format preference for this camera: "Auto" (default) picks the fastest
    /// stream at the chosen size and keeps NV12 when it is already at full rate,
    /// "Nv12" forces uncompressed, "Mjpg" forces compressed (higher frame rate on
    /// bandwidth-limited cameras, at the cost of a decode).
    /// </summary>
    public string PreferredFormat { get; set; } = "Auto";

    /// <summary>Subtype actually selected for the capture stream.</summary>
    private string _chosenSubtype = "NV12";

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
        // A restart must not inherit "a frame already arrived" from the last run.
        Interlocked.Exchange(ref _framesArrived, 0);
        Interlocked.Exchange(ref _firstFrameLogged, 0);

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

        // MJPG: the WinRT reader cannot decode it (it starts and yields no frames),
        // so when MJPG is explicitly requested try the Media Foundation source
        // reader first. It must run BEFORE MediaCapture opens the device, because
        // MediaCapture takes exclusive control and MF could not open it afterwards.
        // Any MF failure falls through to the unchanged WinRT path below.
        bool captureSizeMatches =
            (captureWidth <= 0 || captureWidth == outWidth) &&
            (captureHeight <= 0 || captureHeight == outHeight);

        if (PreferredFormat.Equals("Mjpg", StringComparison.OrdinalIgnoreCase))
        {
            if (!captureSizeMatches)
                AppLog.Write($"Source({DisplayName}): MF-MJPEG skipped (capture {captureWidth}x{captureHeight} != output {outWidth}x{outHeight})");
            else if (await TryStartMfMjpegAsync())
                return true;
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

        // A compressed stream (MJPG) only works if the pipeline can decode it.
        // MediaFrameReader does not decode on its own, so prove frames actually
        // arrive and otherwise revert to uncompressed - a config choice must never
        // leave a camera black.
        if (IsCompressed(_chosenSubtype) && !await WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(2500)))
        {
            AppLog.Write($"Source({DisplayName}): {_chosenSubtype} produced no frames (reader cannot decode it); reverting to uncompressed");

            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;

            await TrySetCaptureFormatAsync(colorSource, captureWidth, captureHeight, forceUncompressed: true);
            _reader = await StartReaderAsync(colorSource);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;
        }

        AppLog.Write($"Source.StartAsync({DisplayName}): reader started");
        Running = true;
        return true;
    }

    /// <summary>
    /// Tries the Media Foundation MJPG backend. Returns true only when it started
    /// AND delivered a frame within 2.5s; on any failure the source is disposed and
    /// the caller continues with the WinRT path.
    /// </summary>
    private async Task<bool> TryStartMfMjpegAsync()
    {
        MfMjpegSource? mf = new MfMjpegSource(DeviceId, DisplayName, OutputWidth, OutputHeight);
        mf.FrameReady += OnMfFrameReady;

        try
        {
            AppLog.Write($"Source({DisplayName}): trying Media Foundation MJPEG backend {OutputWidth}x{OutputHeight}");
            if (!mf.Start())
            {
                AppLog.Write($"Source({DisplayName}): MF-MJPEG failed to start; falling back to WinRT");
                return false;
            }

            if (!await WaitForFirstFrameAsync(TimeSpan.FromMilliseconds(2500)))
            {
                AppLog.Write($"Source({DisplayName}): MF-MJPEG produced no frame in 2.5s; falling back to WinRT");
                return false;
            }

            _mf = mf;
            mf = null; // ownership transferred to the source
            _chosenSubtype = "MJPG";
            Running = true;
            AppLog.Write($"Source.StartAsync({DisplayName}): using Media Foundation MJPEG backend {OutputWidth}x{OutputHeight}");
            return true;
        }
        finally
        {
            if (mf != null)
            {
                mf.FrameReady -= OnMfFrameReady;
                await mf.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Frame callback for the MF backend: publishes the same tight NV12 buffer
    /// contract as the WinRT path, plus the optional 320x180 Bgra8 preview.
    /// </summary>
    private unsafe void OnMfFrameReady(byte[] nv12)
    {
        Interlocked.Increment(ref _framesArrived);
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            AppLog.Write($"Source({DisplayName}): first frame {OutputWidth}x{OutputHeight} NV12 (MF-MJPEG)");

        // Read once per frame: toggling previews mid-stream must not tear a frame.
        bool previews = PreviewEnabled;
        byte[] preview;
        lock (_lock)
            preview = _preview;

        if (previews)
        {
            fixed (byte* src = nv12)
                ColorConverter.Nv12ToBgra8Downscale(
                    src, OutputWidth, OutputHeight, OutputWidth,
                    preview, PreviewWidth, PreviewHeight);
        }

        try
        {
            FrameReady?.Invoke(nv12);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _convertErrors) <= 5)
                AppLog.Write($"Source({DisplayName}): MF-MJPEG frame publish threw {ex.GetType().Name}: {ex.Message}");
        }

        if (previews)
            PreviewReady?.Invoke(preview, PreviewWidth, PreviewHeight);
    }

    /// <summary>
    /// Chooses and applies a capture format whose aspect matches the output, so the
    /// Frame Server does not pillarbox/letterbox, and whose frame rate is the best
    /// that size can sustain. Uncompressed NV12 is preferred (no conversion), but
    /// bandwidth-limited cameras (the C920 over USB 2.0, for example) only reach
    /// their full rate in MJPG, so a compressed stream is used when it is faster.
    /// </summary>
    private async Task TrySetCaptureFormatAsync(MediaFrameSource source, int captureWidth, int captureHeight, bool forceUncompressed = false)
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

            // 1. Pick the size: exact when forced, otherwise best-matching aspect then
            //    closest area, so 16:9 stays 16:9 and nothing gets pillarboxed.
            var size = formats
                .GroupBy(f => (W: f.VideoFormat.Width, H: f.VideoFormat.Height))
                .Select(g => new
                {
                    g.Key.W,
                    g.Key.H,
                    Area = (double)g.Key.W * g.Key.H,
                    AspectErr = Math.Abs((double)g.Key.W / g.Key.H - targetAspect),
                    Rates = g.ToList()
                })
                .OrderBy(s => forced
                    ? (Math.Abs(s.Area - targetArea), s.AspectErr)
                    : (s.AspectErr, Math.Abs(s.Area - targetArea)))
                .First();

            // 2. Pick the subtype + rate at that size.
            var candidates = size.Rates
                .Select(f => new { Format = f, Fps = FpsOf(f), Compressed = IsCompressed(f.Subtype) })
                .OrderByDescending(c => c.Fps)
                .ToList();

            var rawBest = candidates.FirstOrDefault(c => !c.Compressed);
            var overall = candidates[0];
            string prefer = forceUncompressed ? "Nv12" : (PreferredFormat ?? "Auto");

            var chosen =
                prefer.Equals("Mjpg", StringComparison.OrdinalIgnoreCase)
                    ? candidates.FirstOrDefault(c => c.Compressed) ?? overall
                    : prefer.Equals("Nv12", StringComparison.OrdinalIgnoreCase)
                        ? rawBest ?? overall
                        : rawBest != null && rawBest.Fps >= overall.Fps * 0.95
                            ? rawBest
                            : overall;

            var rates = string.Join(", ", candidates.Select(c => $"{c.Format.Subtype}@{c.Fps:0.#}"));
            AppLog.Write($"Source({DisplayName}): {size.W}x{size.H} rates [{rates}] -> {chosen.Format.Subtype}@{chosen.Fps:0.#} (prefer {prefer})");

            _chosenSubtype = chosen.Format.Subtype ?? "NV12";

            var cur = source.CurrentFormat;
            if (cur != null &&
                cur.VideoFormat.Width == chosen.Format.VideoFormat.Width &&
                cur.VideoFormat.Height == chosen.Format.VideoFormat.Height &&
                string.Equals(cur.Subtype, _chosenSubtype, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write($"Source({DisplayName}): capture format already {size.W}x{size.H} {_chosenSubtype}");
                return;
            }

            await source.SetFormatAsync(chosen.Format);
            AppLog.Write($"Source({DisplayName}): capture format -> {size.W}x{size.H} {_chosenSubtype}@{chosen.Fps:0.#} (target {wantW}x{wantH}{(forced ? ", forced" : "")})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): SetFormat failed ({ex.Message}); keeping default");
        }
    }

    private static double FpsOf(MediaFrameFormat f)
    {
        var r = f.FrameRate;
        if (r == null || r.Denominator == 0)
            return 0;
        return (double)r.Numerator / r.Denominator;
    }

    private static bool IsCompressed(string? subtype) =>
        string.Equals(subtype, "MJPG", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subtype, "H264", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subtype, "HEVC", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates and starts a frame reader that produces a decoded color frame.
    /// Requesting Bgra8 (rather than leaving the subtype unset) guarantees
    /// <see cref="VideoMediaFrame.SoftwareBitmap"/> is non-null for cameras whose
    /// native format is compressed (MJPG/H.264).
    /// </summary>
    private async Task<MediaFrameReader> StartReaderAsync(MediaFrameSource colorSource)
    {
        // Uncompressed sources are requested as NV12 at the exact output size, so the
        // Frame Server does any resize + color conversion itself and the hot path is a
        // straight copy. The same request is made for a compressed source (MJPG): the
        // reader can hand back decoded NV12, and NV12 is the one subtype that works
        // for both. Bgra8 is only a fallback, because on a compressed stream it is the
        // path that yields no frames at all.
        try
        {
            var r = await _capture!.CreateFrameReaderAsync(
                colorSource, MediaEncodingSubtypes.Nv12,
                new BitmapSize { Width = (uint)OutputWidth, Height = (uint)OutputHeight });
            await r.StartAsync();
            AppLog.Write($"Source({DisplayName}): frame reader = Nv12 {OutputWidth}x{OutputHeight}" +
                         (IsCompressed(_chosenSubtype) ? $" (decoding {_chosenSubtype})" : ""));
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

    private async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _framesArrived) > 0)
                return true;
            await Task.Delay(50);
        }
        return Volatile.Read(ref _framesArrived) > 0;
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
        Interlocked.Increment(ref _framesArrived);
        if (++_fpsWindowFrames >= 75)
        {
            double secs = (DateTime.UtcNow - _fpsWindowStart).TotalSeconds;
            if (secs >= 4)
            {
                AppLog.Write($"Source({DisplayName}): {_fpsWindowFrames} frames in {secs:0.0}s = {_fpsWindowFrames / secs:0.0} fps");
                _fpsWindowStart = DateTime.UtcNow;
                _fpsWindowFrames = 0;
            }
        }

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

        // Read once per frame: toggling previews mid-stream must not tear a frame.
        bool previews = PreviewEnabled;

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
            if (previews)
                ColorConverter.Nv12ToBgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }
        else
        {
            // Fallback: reader delivered Bgra8 (rare). Legacy conversion path.
            ColorConverter.Bgra8ToNv12(src, w, h, stride, nv12, OutputWidth, OutputHeight);
            FrameReady?.Invoke(nv12);
            if (previews)
                ColorConverter.Bgra8Downscale(src, w, h, stride, preview, PreviewWidth, PreviewHeight);
        }

        if (previews)
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
        if (_mf != null)
        {
            var mf = _mf;
            _mf = null;
            mf.FrameReady -= OnMfFrameReady;
            AppLog.Write($"Source.Dispose({DisplayName}): stopping MF-MJPEG");
            try { mf.Dispose(); } catch (Exception ex) { AppLog.Write($"Source.Dispose({DisplayName}): MF-MJPEG dispose failed {ex.GetType().Name}: {ex.Message}"); }
            AppLog.Write($"Source.Dispose({DisplayName}): MF-MJPEG stopped");
        }

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
        if (_capture != null)
        {
            AppLog.Write($"Source.Dispose({DisplayName}): MediaCapture.Dispose");
            _capture.Dispose();
            _capture = null;
        }
        Running = false;
    }
}
