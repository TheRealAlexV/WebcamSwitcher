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

    // --- Frame-stall watchdog -------------------------------------------------
    // WinRT MediaFrameReader delivery can silently stop after a while (observed as
    // ~0.9 fps with bursts of 75 frames). The pipeline does not notice a dead
    // reader, so the source now watches its own callback liveness and restarts the
    // reader/capture pair instead of leaving a frozen camera until the user toggles
    // capture off and on.
    private readonly SemaphoreSlim _lifecycle = new(1, 1); // serializes start/stop/recovery
    private System.Threading.Timer? _watchdog;
    private long _lastFrameTicks;            // Environment.TickCount64 of the last callback
    private long _startedTicks;              // when the current reader start completed
    private long _framesTotal;               // callbacks since the last (re)start
    private long _stallRecoveries;           // successful restarts (a frame arrived)
    private long _stallDiagLogged;           // one environment dump per source
    private int _recovering;                 // 1 while a recovery runs
    private long _recoveryRestartTicks;      // when the current recovery restart began
    private long _recoveryFirstFramePending; // 1 until the first frame after a restart
    private long _recoveryBackoffMs;         // current backoff step, 0 = none
    private long _nextRecoveryTicks;         // earliest time for the next attempt
    private int _lastCaptureWidth;           // start arguments, replayed on recovery
    private int _lastCaptureHeight;
    private IReadOnlyList<MediaFrameSourceGroup>? _lastGroups;
    private volatile bool _disposed;
    private volatile bool _captureDesired; // true while capture should be running (incl. during recovery)

    private const long StallThresholdMs = 2000;
    private const long StartupGraceMs = 5000;
    private const long RecoveryFirstFrameTimeoutMs = 5000;
    private const long MinRecoveryBackoffMs = 2000;
    private const long MaxRecoveryBackoffMs = 30000;
    private const int RecoverySettleMs = 300;

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
    public bool PreviewEnabled
    {
        get => Volatile.Read(ref _previewEnabled);
        set => Volatile.Write(ref _previewEnabled, value);
    }

    private bool _previewEnabled = true;

    /// <summary>
    /// When true, exposure is pinned to manual 1/30 s after the reader starts so
    /// the camera cannot lengthen integration time in dim light and drop to its
    /// 15 fps mode. The image may be darker; that is the accepted tradeoff.
    /// </summary>
    public bool PreferSmooth30Fps { get; set; }

    /// <summary>
    /// Capture-format preference for this camera: "Auto" (default) picks the fastest
    /// stream at the chosen size and keeps NV12 when it is already at full rate,
    /// "Nv12" forces uncompressed, "Mjpg" forces compressed (higher frame rate on
    /// bandwidth-limited cameras, at the cost of a decode).
    /// </summary>
    public string PreferredFormat { get; set; } = "Auto";

    /// <summary>Subtype actually selected for the capture stream.</summary>
    private string _chosenSubtype = "NV12";

    /// <summary>
    /// Configured output rate. Only used to describe the MF output type; the rate
    /// itself is whatever the camera delivers (the pipes are drop-latest).
    /// </summary>
    public int CaptureFps { get; set; } = Protocol.DefaultFps;

    /// <summary>Successful stall recoveries for this source (a frame arrived after a restart).</summary>
    public long StallRecoveries => Interlocked.Read(ref _stallRecoveries);

    /// <summary>Frame callbacks received since the current capture was started.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesTotal);

    public CameraSource(string deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public async Task<bool> StartAsync(int outWidth, int outHeight, int captureWidth = 0, int captureHeight = 0,
        IReadOnlyList<MediaFrameSourceGroup>? groups = null)
    {
        // Serialized with the watchdog and DisposeAsync: a recovery restart must
        // never run concurrently with a user-initiated start/stop.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Remember the arguments so the watchdog can replay this exact start.
            _lastCaptureWidth = captureWidth;
            _lastCaptureHeight = captureHeight;
            _lastGroups = groups;
            _disposed = false;

            // A user-initiated start is not a recovery: clear any recovery state.
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0);
            Volatile.Write(ref _recoveryRestartTicks, 0);
            Interlocked.Exchange(ref _recoveryBackoffMs, 0);
            Volatile.Write(ref _nextRecoveryTicks, 0);

            _captureDesired = true;
            bool ok = await StartCoreAsync(outWidth, outHeight, captureWidth, captureHeight, groups).ConfigureAwait(false);
            if (!ok)
                _captureDesired = false;
            return ok;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// The actual start path. Callers must hold <see cref="_lifecycle"/>; the
    /// watchdog recovery re-enters here after tearing the old capture down.
    /// </summary>
    private async Task<bool> StartCoreAsync(int outWidth, int outHeight, int captureWidth, int captureHeight,
        IReadOnlyList<MediaFrameSourceGroup>? groups)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        _nv12 = new byte[Protocol.Nv12Size(outWidth, outHeight)];
        _preview = new byte[PreviewWidth * PreviewHeight * 4];
        // A restart must not inherit "a frame already arrived" from the last run.
        Interlocked.Exchange(ref _framesArrived, 0);
        Interlocked.Exchange(ref _firstFrameLogged, 0);
        // ... nor liveness for the watchdog.
        Interlocked.Exchange(ref _framesTotal, 0);
        Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);

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

        // Must come last: selecting a capture format resets the device controls,
        // so pinning exposure any earlier would be silently undone.
        ApplyExposurePreference();

        AppLog.Write($"Source.StartAsync({DisplayName}): reader started");
        Running = true;
        // The watchdog only starts once the reader is up: startup itself can
        // legitimately take seconds (format switch, decode probe).
        Volatile.Write(ref _startedTicks, Environment.TickCount64);
        StartWatchdog();
        return true;
    }

    /// <summary>
    /// Pins exposure to a manual 1/30 s integration time when the user asked for
    /// smooth 30 fps. UVC auto-exposure lengthens exposure in dim light and drops
    /// the camera to its 1/15 s mode (exactly 15 fps); a manual 1/30 s cap keeps
    /// the frame rate at the cost of a darker image. Never throws: a camera
    /// without exposure control must keep capturing normally.
    /// </summary>
    private void ApplyExposurePreference()
    {
        if (!PreferSmooth30Fps)
        {
            RestoreAutoExposure();
            return;
        }

        try
        {
            var exposure = _capture?.VideoDeviceController?.Exposure;
            if (exposure == null || !exposure.Capabilities.Supported)
            {
                AppLog.Write($"Source({DisplayName}): exposure control unsupported; cannot pin 30 fps");
                return;
            }

            var caps = exposure.Capabilities;
            if (caps.Step <= 0)
            {
                AppLog.Write($"Source({DisplayName}): exposure control has no usable step; skipping 30 fps cap");
                return;
            }

            // Read what auto-exposure currently wants while it is still in auto, so a
            // scene that is already correctly exposed is left alone instead of being
            // brightened (and blown out) by a fixed value.
            double autoValue = 0;
            bool haveAuto = caps.AutoModeSupported && exposure.TryGetValue(out autoValue);

            // Some cameras report this control in seconds, others in log2 seconds
            // (the UVC/DirectShow convention, where -5 == 1/32 s). A range that sits
            // at or below zero with small integer values is the log2 form.
            bool log2Units = caps.Max <= 0d && caps.Min < 0d;

            double cap = log2Units ? Math.Log(1.0 / 30.0, 2.0) : 1.0 / 30.0;

            // Only ever shorten the exposure: keep auto's value when it is already
            // shorter than the cap (bright scene), otherwise clamp down to the cap.
            double target = haveAuto ? Math.Min(autoValue, cap) : cap;

            // Snap to the control's grid and keep it inside the advertised range.
            target = caps.Min + Math.Round((target - caps.Min) / caps.Step) * caps.Step;
            target = Math.Max(caps.Min, Math.Min(caps.Max, target));

            if (!exposure.TrySetAuto(false))
            {
                AppLog.Write($"Source({DisplayName}): exposure auto-off failed; cannot cap exposure");
                return;
            }

            if (!exposure.TrySetValue(target))
            {
                AppLog.Write($"Source({DisplayName}): exposure set {target} failed (min={caps.Min} max={caps.Max} step={caps.Step})");
                return;
            }

            double applied = target;
            exposure.TryGetValue(out applied);
            string pretty = log2Units
                ? $"1/{Math.Round(Math.Pow(2, -applied)):0}s"
                : $"{applied:0.0000}s";
            AppLog.Write($"Source({DisplayName}): exposure capped at {applied} ({(log2Units ? "log2 s" : "s")}) = {pretty} for 30 fps (auto wanted {autoValue})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): exposure pinning failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts exposure back under camera control. Only meaningful after
    /// <see cref="ApplyExposurePreference"/> pinned a manual value: leaving the
    /// device in manual would keep the image dark for every other app too, so
    /// turning the option off has to undo it. Never throws.
    /// </summary>
    private void RestoreAutoExposure()
    {
        try
        {
            var exposure = _capture?.VideoDeviceController?.Exposure;
            if (exposure == null || !exposure.Capabilities.Supported || !exposure.Capabilities.AutoModeSupported)
                return;

            if (exposure.TryGetAuto(out bool auto) && auto)
                return; // already automatic: nothing was pinned on this device

            if (exposure.TrySetAuto(true))
                AppLog.Write($"Source({DisplayName}): exposure returned to automatic");
            else
                AppLog.Write($"Source({DisplayName}): exposure auto-on failed; device may still be manual");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): exposure restore failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries the Media Foundation MJPG backend. Returns true only when it started
    /// AND delivered a frame within 2.5s; on any failure the source is disposed and
    /// the caller continues with the WinRT path.
    /// </summary>
    private async Task<bool> TryStartMfMjpegAsync()
    {
        MfMjpegSource? mf = new MfMjpegSource(DeviceId, DisplayName, OutputWidth, OutputHeight, CaptureFps);
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
            if (PreferSmooth30Fps)
                AppLog.Write($"Source({DisplayName}): exposure pinning is not applied on the MF/MJPG path");
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

    // --- Stall watchdog -------------------------------------------------------

    private void StartWatchdog()
    {
        if (_watchdog == null)
            _watchdog = new System.Threading.Timer(OnWatchdogTick, null, 1000, 1000);
    }

    private void StopWatchdog()
    {
        var timer = _watchdog;
        _watchdog = null;
        if (timer != null)
        {
            try { timer.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Runs once per second while a capture is wanted. Cheap: a few volatile reads
    /// and no allocation unless the frame stream has actually gone quiet. The
    /// recovery itself is fire-and-forget so this callback never blocks a pool
    /// thread on I/O.
    /// </summary>
    private void OnWatchdogTick(object? state)
    {
        if (_disposed || !_captureDesired)
            return;

        try
        {
            long now = Environment.TickCount64;
            if (!IsStalled(now, out _))
                return;
            if (now < Volatile.Read(ref _nextRecoveryTicks))
                return;
            if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
                return;

            _ = RecoverFromStallAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _recovering, 0);
            AppLog.Write($"Source({DisplayName}): watchdog tick threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stall rule: no callback for more than 2s, at least one callback since the
    /// last (re)start, and more than 5s since that start (startup grace). A
    /// recovery restart that never delivered a frame counts as stalled too, so
    /// the backoff can retry it.
    /// </summary>
    private bool IsStalled(long now, out long stalledMs)
    {
        stalledMs = now - Volatile.Read(ref _lastFrameTicks);
        bool awaitingFirstFrame = Volatile.Read(ref _recoveryFirstFramePending) == 1;

        if (_reader == null && !awaitingFirstFrame)
            return false;
        if (!awaitingFirstFrame && Volatile.Read(ref _framesTotal) <= 0)
            return false; // nothing has ever arrived: still starting, not stalled

        long sinceStart = awaitingFirstFrame
            ? now - Volatile.Read(ref _recoveryRestartTicks)
            : now - Volatile.Read(ref _startedTicks);
        long grace = awaitingFirstFrame ? RecoveryFirstFrameTimeoutMs : StartupGraceMs;
        if (sinceStart <= grace)
            return false;

        return stalledMs > StallThresholdMs;
    }

    /// <summary>
    /// Restarts this source's capture after a stall. Never throws out (the timer
    /// runs on a pool thread and must not take the process down); every failure is
    /// logged and retried with backoff.
    /// </summary>
    private async Task RecoverFromStallAsync()
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !_captureDesired)
                    return;
                if (!IsStalled(Environment.TickCount64, out long stalledMs))
                    return;

                if (Interlocked.Exchange(ref _stallDiagLogged, 1) == 0)
                    AppLog.Write($"Source({DisplayName}): stall diagnostics - readerMode=Realtime, mediaCaptureNull={_capture is null}, gcHeapMB={GC.GetTotalMemory(false) / 1048576}");

                AppLog.Write($"Source({DisplayName}): STALL - no frame for {stalledMs / 1000.0:0.0}s (frames={Interlocked.Read(ref _framesTotal)}, nulls={Volatile.Read(ref _nullFrames)}, recoveries={Interlocked.Read(ref _stallRecoveries)}); restarting capture");

                // Escalating backoff (2s, 4s, 8s ... capped at 30s) so a camera
                // that cannot be reopened is retried, not hammered.
                long backoff = Math.Min(
                    Math.Max(Interlocked.Read(ref _recoveryBackoffMs) * 2, MinRecoveryBackoffMs),
                    MaxRecoveryBackoffMs);
                Interlocked.Exchange(ref _recoveryBackoffMs, backoff);
                Volatile.Write(ref _nextRecoveryTicks, Environment.TickCount64 + backoff);

                await StopReaderAsync().ConfigureAwait(false);
                // Let the USB device / Frame Server release the handle before reopening.
                await Task.Delay(RecoverySettleMs).ConfigureAwait(false);

                _captureDesired = true;
                Interlocked.Exchange(ref _recoveryFirstFramePending, 1);
                Volatile.Write(ref _recoveryRestartTicks, Environment.TickCount64);

                bool ok;
                try
                {
                    ok = await StartCoreAsync(OutputWidth, OutputHeight, _lastCaptureWidth, _lastCaptureHeight, _lastGroups)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ok = false;
                    AppLog.Write($"Source({DisplayName}): STALL recovery restart threw {ex.GetType().Name}: {ex.Message}");
                }

                if (!ok)
                {
                    // Keep "awaiting first frame" set so the watchdog retries after
                    // the backoff instead of giving up on a dead camera.
                    AppLog.Write($"Source({DisplayName}): STALL recovery failed; next attempt in {backoff / 1000}s");
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Source({DisplayName}): STALL recovery threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    /// <summary>
    /// Tears down the reader and MediaCapture so a fresh pair can be created.
    /// Logs and swallows failures: a half-dead reader must not abort recovery.
    /// </summary>
    private async Task StopReaderAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        Running = false;

        if (reader != null)
        {
            try { reader.FrameArrived -= OnFrameArrived; } catch { }
            try { await reader.StopAsync(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL stop reader threw {ex.GetType().Name}: {ex.Message}"); }
            try { reader.Dispose(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL dispose reader threw {ex.GetType().Name}: {ex.Message}"); }
        }

        if (capture != null)
        {
            try { capture.Dispose(); }
            catch (Exception ex) { AppLog.Write($"Source({DisplayName}): STALL dispose MediaCapture threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>First frame after a recovery restart: clear backoff and log it.</summary>
    private void OnRecoveredFirstFrame()
    {
        Interlocked.Exchange(ref _recoveryBackoffMs, 0);
        Volatile.Write(ref _nextRecoveryTicks, 0);
        long took = Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _recoveryRestartTicks));
        Volatile.Write(ref _recoveryRestartTicks, 0);
        long recoveries = Interlocked.Increment(ref _stallRecoveries);
        AppLog.Write($"Source({DisplayName}): STALL recovered - first frame after {took}ms (recoveries={recoveries})");
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // Liveness for the stall watchdog: no allocation, no logging on the hot path.
        Interlocked.Increment(ref _framesTotal);
        Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
        if (Volatile.Read(ref _recoveryFirstFramePending) == 1 &&
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0) == 1)
            OnRecoveredFirstFrame();

        using var frame = sender.TryAcquireLatestFrame();
        // Disposing the SoftwareBitmap is REQUIRED, not optional: accessing it creates a
        // strong reference the runtime counts against a per-process allocation cap, and
        // Microsoft documents that exceeding that cap makes "the flow of new frames stop"
        // until the GC happens to free some. That is exactly the observed symptom (a slow
        // trickle of frames, both cameras at once, temporary recovery after a restart).
        using var sw = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sw == null)
        {
            if ((Interlocked.Increment(ref _nullFrames) & 0x3F) == 1)
                AppLog.Write($"Source({DisplayName}): null SoftwareBitmap x{_nullFrames}");
            return;
        }
        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
            AppLog.Write($"Source({DisplayName}): first frame {sw.PixelWidth}x{sw.PixelHeight} {sw.BitmapPixelFormat}");
        Interlocked.Increment(ref _framesArrived);
        _fpsWindowFrames++;
        // Time-based window: the old code only emitted once 75 frames had piled up, so a
        // stalled camera logged "75 frames in 81.0s" and hid the stall for minutes.
        double secs = (DateTime.UtcNow - _fpsWindowStart).TotalSeconds;
        if (secs >= 4.0)
        {
            AppLog.Write($"Source({DisplayName}): {_fpsWindowFrames} frames in {secs:0.0}s = {_fpsWindowFrames / secs:0.0} fps");
            _fpsWindowStart = DateTime.UtcNow;
            _fpsWindowFrames = 0;
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
        // Serialized with StartAsync and the watchdog recovery so a restart cannot
        // resurrect a source that is being disposed.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _captureDesired = false;
            Interlocked.Exchange(ref _recoveryFirstFramePending, 0);
            StopWatchdog();
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async ValueTask DisposeCoreAsync()
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
