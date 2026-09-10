using WebcamSwitcher.Core;

namespace WebcamSwitcher.App;

/// <summary>
/// Owns the virtual camera, frame publisher, capture engine, and hotkeys, and
/// applies config changes in-process (no app restart): hotkeys re-register
/// live, camera/format changes rebuild the pipeline, format changes also write
/// the format file and restart the virtual camera.
/// </summary>
public sealed class PipelineController : IAsyncDisposable
{
    private AppConfig _config = new();
    private readonly VirtualCameraService _vcam = new();
    private FramePublisher? _publisher;
    private CameraEngine? _engine;
    private HotkeyService? _hotkeys;
    private bool _vcamOk;

    public AppConfig Config => _config;
    public bool VirtualCameraActive => _vcamOk;
    public CameraEngine? Engine => _engine;
    public IReadOnlyList<CameraSource> Sources => _engine?.Sources ?? Array.Empty<CameraSource>();

    public int ActiveIndex
    {
        get => _engine?.ActiveIndex ?? 0;
        set
        {
            if (_engine != null)
                _engine.ActiveIndex = Math.Clamp(value, 0, Math.Max(0, _engine.Sources.Count - 1));
        }
    }

    /// <summary>Raised when the source set changed (UI should rebind previews/tray).</summary>
    public event Action? SourcesChanged;

    public async Task StartAsync(AppConfig config)
    {
        _config = config;
        _config.Normalize();
        SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);
        ApplyStartWithWindows(_config.StartWithWindows);

        _vcamOk = _vcam.Start();
        await RebuildPipelineAsync();
        RebuildHotkeys();
    }

    public async Task ApplyAsync(AppConfig next)
    {
        next.Normalize();

        bool formatChanged = next.Width != _config.Width || next.Height != _config.Height || next.Fps != _config.Fps;
        bool camerasChanged = !CamerasEqual(next.Cameras, _config.Cameras);
        bool hotkeysChanged = !HotkeysEqual(next, _config);
        bool startupChanged = next.StartWithWindows != _config.StartWithWindows;

        _config = next;

        if (formatChanged)
        {
            SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);
            _vcam.Stop();
            _vcamOk = _vcam.Start();
        }

        bool engineChanged = formatChanged || camerasChanged;
        if (engineChanged)
        {
            await RebuildPipelineAsync();
            SourcesChanged?.Invoke();
        }

        if (engineChanged || hotkeysChanged)
            RebuildHotkeys();

        if (startupChanged)
            ApplyStartWithWindows(_config.StartWithWindows);
    }

    private async Task RebuildPipelineAsync()
    {
        if (_engine != null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        var oldPublisher = _publisher;
        _publisher = null;
        oldPublisher?.Dispose();

        _publisher = new FramePublisher(_config.Width, _config.Height, _config.Fps);
        var engine = new CameraEngine(_publisher);
        await engine.StartAsync(_config);
        _engine = engine;
    }

    private void RebuildHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (_engine != null)
        {
            _hotkeys = new HotkeyService(_config, _engine);
            _hotkeys.Register();
        }
    }

    private static bool CamerasEqual(IReadOnlyList<CameraConfig> a, IReadOnlyList<CameraConfig> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].DeviceId, b[i].DeviceId, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool HotkeysEqual(AppConfig a, AppConfig b)
    {
        if (!string.Equals(a.RotateHotkey, b.RotateHotkey, StringComparison.Ordinal))
            return false;
        var ah = a.Hotkeys ?? Array.Empty<string>();
        var bh = b.Hotkeys ?? Array.Empty<string>();
        if (ah.Length != bh.Length)
            return false;
        for (int i = 0; i < ah.Length; i++)
            if (!string.Equals(ah[i], bh[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null)
                return;
            if (enabled)
                key.SetValue("WebcamSwitcher", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("WebcamSwitcher", throwOnMissingValue: false);
        }
        catch
        {
            // Best effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (_engine != null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        var oldPublisher = _publisher;
        _publisher = null;
        oldPublisher?.Dispose();
        _vcam.Dispose();
    }
}
