using Windows.Devices.Enumeration;
using Windows.Media.Capture.Frames;

namespace WebcamSwitcher.Core;

/// <summary>
/// Captures all configured cameras, normalizes the active one, and publishes
/// its frames to the virtual camera. Switching is instant (changes which source
/// drives the publisher).
/// </summary>
public sealed class CameraEngine : IAsyncDisposable
{
    private readonly List<CameraSource> _sources = new();
    // Maps a compact source index (index into _sources / Sources, used by the UI)
    // to its original config.Cameras slot. Populated as sources start successfully
    // so that a camera failing to start never shifts the slot numbering.
    private readonly List<int> _slotFor = new();
    private readonly IReadOnlyList<FramePublisher> _publishers;
    private readonly FramePublisher _switcher;
    private int _activeIndex;
    private int _activeSlot = -1;
    private ulong _sequence;

    // Per-slot delay stage. One pass-through-bypassed delay line per config slot,
    // so every virtual camera this app exposes can hold the same latency and
    // switching in OBS never shifts A/V sync. The delay is content only: it sits
    // between the capture callback and the publishers, upstream of the native
    // source, which never learns about it and keeps its own sample timing.
    private FrameDelayBuffer?[] _delays = Array.Empty<FrameDelayBuffer?>();
    private int[] _slotDelayMs = Array.Empty<int>();
    private int[] _slotFps = Array.Empty<int>();
    private int _commonDelayMs;

    public IReadOnlyList<CameraSource> Sources => _sources;

    public int ActiveIndex
    {
        get => Volatile.Read(ref _activeIndex);
        set
        {
            Volatile.Write(ref _activeIndex, value);
            int slot = value >= 0 && value < _slotFor.Count ? _slotFor[value] : -1;
                Volatile.Write(ref _activeSlot, slot);
            }
    }

    /// <summary>Config slot whose frames should be mirrored to the switcher pipe.</summary>
    private int ActiveSlot => Volatile.Read(ref _activeSlot);

    public CameraSource? ActiveSource
    {
        get
        {
            int i = ActiveIndex;
            return i >= 0 && i < _sources.Count ? _sources[i] : null;
        }
    }

    private volatile bool _previewsEnabled = true;
    private volatile bool _previewsVisible = true;

    /// <summary>
    /// Propagated to every source. When false, per-frame preview downscaling is
    /// skipped while capture and the passthrough virtual cameras keep running.
    /// </summary>
    public bool PreviewsEnabled
    {
        get => _previewsEnabled;
        set
        {
            _previewsEnabled = value;
            PushPreviewState();
        }
    }

    /// <summary>
    /// False while the app window is minimised or hidden: previews then cost
    /// nothing, because nobody can see them. Combined with
    /// <see cref="PreviewsEnabled"/>; capture and publishing are unaffected.
    /// </summary>
    public bool PreviewsVisible
    {
        get => _previewsVisible;
        set
        {
            if (_previewsVisible == value)
                return;
            _previewsVisible = value;
            PushPreviewState();
            AppLog.Write($"Previews: window {(value ? "visible" : "hidden")}, conversion {(value && _previewsEnabled ? "on" : "off")}");
        }
    }

    private void PushPreviewState()
    {
        bool on = _previewsEnabled && _previewsVisible;
        // Snapshot: StartAsync may still be assembling sources on its own thread.
        foreach (var s in _sources.ToArray())
            s.PreviewEnabled = on;
    }

    /// <param name="publishers">
    /// Passthrough publishers aligned with <c>config.Cameras</c>: <c>publishers[slot]</c>
    /// serves the continuous feed for camera slot <c>slot</c>.
    /// </param>
    /// <param name="switcher">Publisher for the active-source switcher feed.</param>
    public CameraEngine(IReadOnlyList<FramePublisher> publishers, FramePublisher switcher)
    {
        _publishers = publishers;
        _switcher = switcher;
        EnsureDelaySlots(publishers.Count);
    }

    /// <summary>
    /// Sets one camera slot's own delay in milliseconds (default 0). The change
    /// takes effect on the next published frame: no engine rebuild, no restart of
    /// any virtual camera.
    /// </summary>
    public void SetSlotDelay(int slot, int ms)
    {
        if (slot < 0 || slot >= _slotDelayMs.Length)
        {
            AppLog.Write($"Engine.SetSlotDelay({slot}): no such camera slot");
            return;
        }
        Volatile.Write(ref _slotDelayMs[slot], Math.Clamp(ms, 0, AppConfig.MaxDelayMs));
        ApplyDelay(slot);
    }

    /// <summary>
    /// Sets the delay added to every camera, in milliseconds (default 0). Applied
    /// live to every slot, exactly like <see cref="SetSlotDelay"/>.
    /// </summary>
    public void SetCommonDelay(int ms)
    {
        Volatile.Write(ref _commonDelayMs, Math.Clamp(ms, 0, AppConfig.MaxDelayMs));
        for (int i = 0; i < _delays.Length; i++)
            ApplyDelay(i);
    }

    /// <summary>
    /// Recomputes one slot's ring depth from its own delay plus the common delay,
    /// in frames at that camera's rate. Depth 0 frees the ring, so an unset delay
    /// holds no memory and copies nothing.
    /// </summary>
    private void ApplyDelay(int slot)
    {
        if (slot < 0 || slot >= _delays.Length)
            return;
        var buffer = _delays[slot];
        if (buffer == null)
            return;

        int ms = Volatile.Read(ref _slotDelayMs[slot]) + Volatile.Read(ref _commonDelayMs);
        int fps = slot < _slotFps.Length ? _slotFps[slot] : 0;
        int depth = DelayFrames(ms, fps);
        if (buffer.Depth == depth)
            return;   // unchanged: nothing to reconfigure, nothing to log

        buffer.Configure(depth);
        AppLog.Write($"Delay: slot {slot} {ms} ms @ {(fps > 0 ? fps : Protocol.DefaultFps)} fps -> depth {depth}");
    }

    /// <summary>
    /// Milliseconds to whole frames at the camera's advertised rate. The rate is
    /// the one requested for capture; a source that reports nothing usable falls
    /// back to <see cref="Protocol.DefaultFps"/>.
    /// </summary>
    private static int DelayFrames(int delayMs, int fps)
    {
        if (delayMs <= 0)
            return 0;
        if (fps <= 0)
            fps = Protocol.DefaultFps;
        return Math.Clamp((int)Math.Round(delayMs * (double)fps / 1000.0), 0, FrameDelayBuffer.MaxDepth);
    }

    /// <summary>
    /// Guarantees the per-slot delay arrays cover <paramref name="count"/> slots,
    /// keeping any delay already configured.
    /// </summary>
    private void EnsureDelaySlots(int count)
    {
        if (_delays.Length >= count && _slotDelayMs.Length >= count && _slotFps.Length >= count)
            return;

        int size = Math.Max(count, _delays.Length);
        var delays = new FrameDelayBuffer?[size];
        var ms = new int[size];
        var fps = new int[size];
        Array.Copy(_delays, delays, _delays.Length);
        Array.Copy(_slotDelayMs, ms, _slotDelayMs.Length);
        Array.Copy(_slotFps, fps, _slotFps.Length);
        _delays = delays;
        _slotDelayMs = ms;
        _slotFps = fps;
    }

    /// <summary>Enumerates available video capture devices (id + name).</summary>
    public static async Task<List<CameraConfig>> EnumerateCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.Select(d => new CameraConfig { DeviceId = d.Id, FriendlyName = d.Name }).ToList();
    }

    public async Task<int> StartAsync(AppConfig config)
    {
        int w = config.Width, h = config.Height;
        int count = config.Cameras.Count;

        // Delay lines are sized before any source starts: Publish runs on each
        // camera's callback thread from its first frame on.
        EnsureDelaySlots(count);
        Volatile.Write(ref _commonDelayMs, Math.Clamp(config.CommonDelayMs, 0, AppConfig.MaxDelayMs));
        for (int i = 0; i < count; i++)
        {
            Volatile.Write(ref _slotDelayMs[i],
                Math.Clamp(config.Cameras[i].DelayMs, 0, AppConfig.MaxDelayMs));
            _delays[i] ??= new FrameDelayBuffer();
        }

        // Enumerate the frame source groups once and share the list with every
        // camera, instead of each CameraSource performing its own device scan.
        IReadOnlyList<MediaFrameSourceGroup> groups = await MediaFrameSourceGroup.FindAllAsync();

        // Create every source first, then start them all concurrently. Index i in
        // these arrays corresponds to config.Cameras[i].
        var sources = new CameraSource?[count];
        var tasks = new Task<bool>[count];

        for (int i = 0; i < count; i++)
        {
            var c = config.Cameras[i];
            if (string.IsNullOrEmpty(c.DeviceId))
                continue;

            var source = new CameraSource(c.DeviceId, c.FriendlyName);
            source.PreviewEnabled = _previewsEnabled && _previewsVisible;
            source.PreferSmooth30Fps = config.PreferSmooth30Fps;
            source.CaptureFps = config.Fps;
            source.PreferredFormat = string.IsNullOrWhiteSpace(c.CaptureFormat) ? "Auto" : c.CaptureFormat;
            _slotFps[i] = source.CaptureFps;
            ApplyDelay(i);
            int slot = i;
            source.FrameReady += nv12 => Publish(slot, nv12, w, h);
            sources[i] = source;

            AppLog.Write($"Engine.StartAsync: starting camera {i} '{c.FriendlyName}'");
            tasks[i] = StartOneAsync(source, w, h, c, groups);
        }

        // A failure of one camera must not prevent the others from starting, so
        // await them together and inspect each result individually.
        await Task.WhenAll(tasks);

        // Assemble _sources in the ORIGINAL configured order so index i still maps
        // to camera i (ActiveIndex, tray menu, and previews depend on this).
        for (int i = 0; i < count; i++)
        {
            var source = sources[i];
            if (source == null)
                continue;

            if (tasks[i].Result)
            {
                _sources.Add(source);
                _slotFor.Add(i);
                AppLog.Write($"Engine.StartAsync: camera {i} started");
            }
            else
            {
                await source.DisposeAsync();
                AppLog.Write($"Engine.StartAsync: camera {i} FAILED to start");
            }
        }

        // Every publisher (each passthrough + the switcher) is started exactly once.
        foreach (var p in _publishers)
            p.Start();
        _switcher.Start();

        ActiveIndex = Math.Clamp(config.ActiveIndex, 0, Math.Max(0, _sources.Count - 1));
        return _sources.Count;
    }

    /// <summary>
    /// Starts a single source, converting a thrown exception into a false result
    /// so one camera's failure cannot abort the others.
    /// </summary>
    private static async Task<bool> StartOneAsync(
        CameraSource source, int w, int h, CameraConfig c, IReadOnlyList<MediaFrameSourceGroup> groups)
    {
        try
        {
            return await source.StartAsync(w, h, c.CaptureWidth ?? 0, c.CaptureHeight ?? 0, groups);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Engine.StartAsync({c.FriendlyName}): start threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void Publish(int slot, byte[] nv12, int w, int h)
    {
        // Delay stage, ahead of both publishes below so the passthrough pipe and
        // the switcher always see the same frame. While a ring is filling it
        // reports false and the frame that just arrived goes out immediately, so
        // the delay can never stall a virtual camera; once full it hands back the
        // frame from Depth frames ago, strictly in order.
        if (slot >= 0 && slot < _delays.Length)
        {
            var delay = _delays[slot];
            if (delay != null && delay.TryPush(nv12, out byte[]? delayed) && delayed != null)
                nv12 = delayed;
        }

        var info = new WsFrameInfo
        {
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampQpc = 0,
            Width = (uint)w,
            Height = (uint)h,
            Stride = (uint)w,
            Format = Protocol.FormatNv12,
            PayloadBytes = (uint)nv12.Length,
            Flags = Protocol.FlagNone
        };

        // Continuous passthrough: every slot always drives its own pipe, but only
        // while something reads it. Skipping the publish skips its frame copy;
        // a vcam that connects later simply starts with the next frame.
        if (slot >= 0 && slot < _publishers.Count)
        {
            var passthrough = _publishers[slot];
            if (passthrough.ClientCount > 0)
                passthrough.PublishFrame(nv12, info);
        }

        // The switcher mirrors whichever config slot is currently active.
        if (slot == ActiveSlot)
            _switcher.PublishFrame(nv12, info);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            AppLog.Write($"Engine.Dispose: disposing source '{s.DisplayName}'");
            await s.DisposeAsync();
            AppLog.Write($"Engine.Dispose: source disposed '{s.DisplayName}'");
        }
        _sources.Clear();
        _slotFor.Clear();
        Volatile.Write(ref _activeSlot, -1);

        // Release the delay rings: on disable a slot must hold no frame memory.
        foreach (var delay in _delays)
            delay?.Configure(0);

        // Dispose every publisher (each passthrough + the switcher). Dispose is
        // idempotent, so the owning PipelineController disposing them again is safe.
        foreach (var p in _publishers)
            p.Dispose();
        _switcher.Dispose();
    }
}
