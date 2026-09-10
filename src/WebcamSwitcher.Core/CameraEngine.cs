using Windows.Devices.Enumeration;

namespace WebcamSwitcher.Core;

/// <summary>
/// Captures all configured cameras, normalizes the active one, and publishes
/// its frames to the virtual camera. Switching is instant (changes which source
/// drives the publisher).
/// </summary>
public sealed class CameraEngine : IAsyncDisposable
{
    private readonly List<CameraSource> _sources = new();
    private readonly FramePublisher _publisher;
    private int _activeIndex;
    private ulong _sequence;

    public IReadOnlyList<CameraSource> Sources => _sources;

    public int ActiveIndex
    {
        get => Volatile.Read(ref _activeIndex);
        set => Volatile.Write(ref _activeIndex, value);
    }

    public CameraSource? ActiveSource
    {
        get
        {
            int i = ActiveIndex;
            return i >= 0 && i < _sources.Count ? _sources[i] : null;
        }
    }

    public CameraEngine(FramePublisher publisher)
    {
        _publisher = publisher;
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
        for (int i = 0; i < config.Cameras.Count; i++)
        {
            var c = config.Cameras[i];
            if (string.IsNullOrEmpty(c.DeviceId))
                continue;

            int index = i;
            var source = new CameraSource(c.DeviceId, c.FriendlyName);
            source.FrameReady += nv12 =>
            {
                if (ActiveIndex == index)
                    Publish(nv12, w, h);
            };

            if (await source.StartAsync(w, h))
                _sources.Add(source);
            else
                await source.DisposeAsync();
        }

        _publisher.Start();
        ActiveIndex = Math.Clamp(config.ActiveIndex, 0, Math.Max(0, _sources.Count - 1));
        return _sources.Count;
    }

    private void Publish(byte[] nv12, int w, int h)
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
        _publisher.PublishFrame(nv12, info);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
            await s.DisposeAsync();
        _sources.Clear();
        _publisher.Dispose();
    }
}
