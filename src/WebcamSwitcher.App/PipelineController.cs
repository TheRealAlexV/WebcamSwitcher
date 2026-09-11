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
    private readonly List<FramePublisher> _publishers = new();
    private FramePublisher? _switcher;
    private CameraEngine? _engine;
    private HotkeyService? _hotkeys;
    private bool _vcamOk;

    public AppConfig Config => _config;
    public bool VirtualCameraActive => _vcamOk;
    public bool CaptureActive => _engine != null;
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
    /// <summary>Raised when the virtual camera is started or stopped.</summary>
    public event Action? VirtualCameraStateChanged;
    /// <summary>Raised when capture is started or stopped.</summary>
    public event Action? CaptureStateChanged;

    public async Task StartAsync(AppConfig config)
    {
        _config = config;
        _config.Normalize();
        SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);
        ApplyStartWithWindows(_config.StartWithWindows);

        // The virtual camera starts disabled unless the user opted in. The native
        // registration is slow, so run it off the UI thread (RebuildHotkeys below
        // must stay on the UI thread).
        if (_config.VirtualCameraOnLaunch)
            _vcamOk = await Task.Run(() => _vcam.Start(_config.Cameras));
        await RebuildPipelineAsync();
        RebuildHotkeys();

        // Initial start populates sources/vcam/capture after the UI may already
        // be visible (the window is shown eagerly during startup), so notify.
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
        VirtualCameraStateChanged?.Invoke();
    }

    public async Task ApplyAsync(AppConfig next)
    {
        next.Normalize();
        AppLog.Write($"ApplyAsync begin format={next.Width}x{next.Height}@{next.Fps} cams={next.Cameras.Count}");

        bool formatChanged = next.Width != _config.Width || next.Height != _config.Height || next.Fps != _config.Fps;
        bool camerasChanged = !CamerasEqual(next.Cameras, _config.Cameras);
        bool hotkeysChanged = !HotkeysEqual(next, _config);
        bool startupChanged = next.StartWithWindows != _config.StartWithWindows;

        _config = next;

        if (formatChanged)
            SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);

        bool engineChanged = formatChanged || camerasChanged;
        if (engineChanged)
        {
            AppLog.Write("ApplyAsync: engine changed -> rebuild");
            await RebuildPipelineAsync();
            AppLog.Write("ApplyAsync: rebuild done");
            SourcesChanged?.Invoke();
        }

        if (formatChanged && _vcamOk)
        {
            // Restart the virtual camera (off the UI thread) so it re-advertises
            // the new format. Done after the engine rebuild so the physical
            // cameras are already released. Only restarted if it was already on.
            await Task.Run(() =>
            {
                AppLog.Write("ApplyAsync: vcam Stop begin");
                _vcam.Stop();
                AppLog.Write("ApplyAsync: vcam Stop done");
                _vcamOk = _vcam.Start(_config.Cameras);
                AppLog.Write("ApplyAsync: vcam Start done");
            });
            AppLog.Write("ApplyAsync: vcam restarted");
        }

        if (engineChanged || hotkeysChanged)
        {
            AppLog.Write("ApplyAsync: rebuild hotkeys");
            RebuildHotkeys();
        }

        if (startupChanged)
            ApplyStartWithWindows(_config.StartWithWindows);

        AppLog.Write("ApplyAsync end");
    }

    public void StartVirtualCamera()
    {
        if (_vcamOk)
            return;
        _vcamOk = _vcam.Start(_config.Cameras);
        AppLog.Write($"StartVirtualCamera: {(_vcamOk ? "ok" : "failed")}");
        VirtualCameraStateChanged?.Invoke();
    }

    public void StopVirtualCamera()
    {
        if (!_vcamOk)
            return;
        _vcam.Stop();
        _vcamOk = false;
        AppLog.Write("StopVirtualCamera");
        VirtualCameraStateChanged?.Invoke();
    }

    public async Task StartCaptureAsync()
    {
        if (_engine != null)
            return;
        AppLog.Write("StartCapture");
        await RebuildPipelineAsync();
        RebuildHotkeys();
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
    }

    public async Task StopCaptureAsync()
    {
        if (_engine == null)
            return;
        AppLog.Write("StopCapture");
        await Task.Run(async () =>
        {
            if (_engine != null)
            {
                await _engine.DisposeAsync();
                _engine = null;
            }
            DisposePublishers();
        });
        RebuildHotkeys();
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
    }

    private async Task RebuildPipelineAsync()
    {
        // Run camera teardown/init off the UI thread so a slow or stuck capture
        // operation can never freeze the UI.
        await Task.Run(RebuildPipelineCoreAsync);
    }

    private async Task RebuildPipelineCoreAsync()
    {
        if (_engine != null)
        {
            AppLog.Write("Rebuild: dispose engine begin");
            await _engine.DisposeAsync();
            AppLog.Write("Rebuild: dispose engine end");
            _engine = null;
        }
        AppLog.Write("Rebuild: dispose publishers begin");
        DisposePublishers();
        AppLog.Write("Rebuild: dispose publishers end");

        // One passthrough publisher per camera slot plus the switcher publisher.
        int w = _config.Width, h = _config.Height, fps = _config.Fps;
        for (int i = 0; i < _config.Cameras.Count; i++)
            _publishers.Add(new FramePublisher(Protocol.PassthroughPipeName(i), w, h, fps));
        _switcher = new FramePublisher(Protocol.PipeName, w, h, fps);

        var engine = new CameraEngine(_publishers, _switcher);
        AppLog.Write("Rebuild: engine StartAsync begin");
        await engine.StartAsync(_config);
        AppLog.Write("Rebuild: engine StartAsync end");
        _engine = engine;
    }

    private void DisposePublishers()
    {
        foreach (var p in _publishers)
        {
            try { p.Dispose(); } catch { }
        }
        _publishers.Clear();
        try { _switcher?.Dispose(); } catch { }
        _switcher = null;
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

    /// <summary>Disposes UI-affine hotkey resources. Must be called on the UI thread.</summary>
    public void DisposeHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
    }

    public async ValueTask DisposeAsync()
    {
        // Hotkeys are UI-affine; the caller (OnExit) disposes them on the UI
        // thread via DisposeHotkeys() first. This is a fallback for other paths.
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (_engine != null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        DisposePublishers();
        _vcam.Dispose();
    }
}
