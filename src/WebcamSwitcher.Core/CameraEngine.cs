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

    /// <param name="publishers">
    /// Passthrough publishers aligned with <c>config.Cameras</c>: <c>publishers[slot]</c>
    /// serves the continuous feed for camera slot <c>slot</c>.
    /// </param>
    /// <param name="switcher">Publisher for the active-source switcher feed.</param>
    public CameraEngine(IReadOnlyList<FramePublisher> publishers, FramePublisher switcher)
    {
        _publishers = publishers;
        _switcher = switcher;
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

        // Continuous passthrough: every slot always drives its own pipe.
        if (slot >= 0 && slot < _publishers.Count)
            _publishers[slot].PublishFrame(nv12, info);

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

        // Dispose every publisher (each passthrough + the switcher). Dispose is
        // idempotent, so the owning PipelineController disposing them again is safe.
        foreach (var p in _publishers)
            p.Dispose();
        _switcher.Dispose();
    }
}
