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
    private VirtualCameraService _vcam = new();
    private readonly List<FramePublisher> _publishers = new();
    private FramePublisher? _switcher;
    private CameraEngine? _engine;
    private HotkeyService? _hotkeys;

    // Per-slot virtual camera state. Gates serialise calls for the same slot
    // (a native create/stop takes tens to hundreds of ms and must not overlap
    // itself); different slots have different gates and proceed independently.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _vcamGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _vcamBusy = new();
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public AppConfig Config => _config;

    /// <summary>True while at least one virtual camera (any slot) is running.</summary>
    public bool VirtualCameraActive => _vcam.IsRunning;
    public bool CaptureActive => _engine != null;
    public bool PreviewsActive => _config.PreviewsEnabled;

    // Whether the app window is on screen. Kept here so a freshly created engine
    // inherits it; capture and publishing are never gated on it.
    private volatile bool _previewsVisible = true;
    public CameraEngine? Engine => _engine;
    public IReadOnlyList<CameraSource> Sources => _engine?.Sources ?? Array.Empty<CameraSource>();

    public int ActiveIndex
    {
        get => _engine?.ActiveIndex ?? 0;
        set
        {
            int count = _engine?.Sources.Count ?? _config.Cameras.Count;
            int index = Math.Clamp(value, 0, Math.Max(0, count - 1));
            if (_engine != null)
                _engine.ActiveIndex = index;
            PersistActiveIndex(index);
        }
    }

    /// <summary>
    /// Saves which camera is live so a restart resumes on the same feed. Cheap
    /// enough to run on every switch, which keeps the tray, the hotkeys and a
    /// click on a preview tile all in agreement.
    /// </summary>
    private void PersistActiveIndex(int index)
    {
        if (_config.ActiveIndex == index)
            return;
        _config.ActiveIndex = index;
        try { ConfigService.Save(_config); }
        catch (Exception ex) { AppLog.Write($"ActiveIndex: save failed {ex.Message}"); }
    }

    /// <summary>
    /// Test hook: adopts a config and a service with a fake native layer, without
    /// starting capture, hotkeys or touching the on-disk audio/format files.
    /// </summary>
    internal void InitializeForTest(AppConfig config, VirtualCameraService vcam)
    {
        _config = config;
        _config.Normalize();
        _vcam = vcam;
    }

    /// <summary>Raised when the source set changed (UI should rebind previews/tray).</summary>
    public event Action? SourcesChanged;
    /// <summary>Raised when the virtual camera is started or stopped.</summary>
    public event Action? VirtualCameraStateChanged;
    /// <summary>Raised when capture is started or stopped.</summary>
    public event Action? CaptureStateChanged;

    /// <summary>Raised when camera previews are turned on or off.</summary>
    public event Action? PreviewsStateChanged;

    /// <summary>
    /// Raised on the UI thread after every virtual-camera state change: a slot
    /// starting to be busy, finishing busy, and its resulting running state.
    /// </summary>
    public event Action? VcamStateChanged;

    /// <summary>Number of per-camera virtual camera slots (the switcher is extra).</summary>
    public int CameraCount => _config.Cameras.Count;

    /// <summary>Slot index of the switcher virtual camera.</summary>
    private int SwitcherSlot => _config.Cameras.Count;

    /// <summary>
    /// True when that camera's passthrough virtual camera is live. False for an
    /// out-of-range index, a camera with no virtual camera, or a stopped slot.
    /// </summary>
    public bool IsVcamRunning(int cameraIndex) =>
        cameraIndex >= 0 && cameraIndex < CameraCount && _vcam.IsSlotRunning(cameraIndex);

    /// <summary>True when the switcher virtual camera is live.</summary>
    public bool IsSwitcherVcamRunning => _vcam.IsSlotRunning(SwitcherSlot);

    /// <summary>True while a start/stop for that camera slot is in flight.</summary>
    public bool IsVcamBusy(int cameraIndex) =>
        cameraIndex >= 0 && cameraIndex < CameraCount && BusyCount(cameraIndex) > 0;

    /// <summary>True while a start/stop for the switcher slot is in flight.</summary>
    public bool IsSwitcherVcamBusy => BusyCount(SwitcherSlot) > 0;

    /// <summary>
    /// Starts or stops one camera's passthrough virtual camera off the UI thread
    /// and returns the resulting state. Calls for the same slot are serialised;
    /// different slots run independently. Never throws.
    /// </summary>
    public Task<bool> SetVcamRunningAsync(int cameraIndex, bool running)
    {
        if (cameraIndex < 0 || cameraIndex >= CameraCount)
        {
            AppLog.Write($"SetVcamRunningAsync({cameraIndex}): no such camera slot");
            return Task.FromResult(false);
        }
        return SetSlotRunningAsync(cameraIndex, running, isSwitcher: false, persist: true);
    }

    /// <summary>Starts or stops the switcher virtual camera off the UI thread.</summary>
    public Task<bool> SetSwitcherVcamRunningAsync(bool running) =>
        SetSlotRunningAsync(SwitcherSlot, running, isSwitcher: true, persist: true);

    /// <summary>
    /// Starts or stops the whole set. Turning on starts exactly the slots marked
    /// enabled (<see cref="CameraConfig.VirtualCameraEnabled"/> and
    /// <see cref="AppConfig.SwitcherVirtualCameraEnabled"/>); individually
    /// disabled slots stay off. Turning off stops every slot. Returns whether
    /// every attempted slot reached the requested state.
    /// </summary>
    public async Task<bool> SetAllVcamsRunningAsync(bool running)
    {
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < CameraCount; i++)
        {
            if (running && !_config.Cameras[i].VirtualCameraEnabled)
                continue;
            // The global toggle is not a per-slot choice: it starts the slots the
            // user left enabled and never rewrites their individual flags.
            tasks.Add(SetSlotRunningAsync(i, running, isSwitcher: false, persist: false));
        }

        if (!running || _config.SwitcherVirtualCameraEnabled)
            tasks.Add(SetSlotRunningAsync(SwitcherSlot, running, isSwitcher: true, persist: false));

        if (tasks.Count == 0)
        {
            AppLog.Write("SetAllVcamsRunningAsync: every slot is individually disabled");
            return true;
        }

        bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        bool ok = results.All(r => r);
        AppLog.Write($"SetAllVcamsRunningAsync({running}): {(ok ? "ok" : "partial failure")} ({results.Count(r => r)}/{results.Length} slots)");
        return ok;
    }

    private async Task<bool> SetSlotRunningAsync(int slot, bool running, bool isSwitcher, bool persist)
    {
        bool ok = false;
        EnterBusy(slot);
        RaiseVcamStateChanged();
        try
        {
            var gate = _vcamGates.GetOrAdd(slot, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Names/ini for every slot are prepared before any creation; cheap
                // and idempotent, and it keeps a late start on the same mapping.
                _vcam.Prepare(_config.Cameras);
                string? name = _vcam.SlotName(slot);

                if (running)
                {
                    ok = await Task.Run(() => _vcam.StartSlot(slot)).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => _vcam.StopSlot(slot)).ConfigureAwait(false);
                    ok = !_vcam.IsSlotRunning(slot);
                }

                AppLog.Write($"Vcam slot {slot} ('{name}') {(running ? "start" : "stop")} -> {(ok ? "running ok" : "failed")}");
            }
            finally
            {
                gate.Release();
            }

            if (ok && persist)
                PersistVcamChoice(slot, running, isSwitcher);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Vcam slot {slot} {(running ? "start" : "stop")} threw {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }
        finally
        {
            ExitBusy(slot);
            RaiseVcamStateChanged();
            RaiseVirtualCameraStateChanged();
        }
        return ok;
    }

    /// <summary>Saves the per-slot choice through the normal config path.</summary>
    private void PersistVcamChoice(int slot, bool running, bool isSwitcher)
    {
        try
        {
            if (isSwitcher)
            {
                if (_config.SwitcherVirtualCameraEnabled == running)
                    return;
                _config.SwitcherVirtualCameraEnabled = running;
            }
            else
            {
                if (slot < 0 || slot >= _config.Cameras.Count)
                    return;
                if (_config.Cameras[slot].VirtualCameraEnabled == running)
                    return;
                _config.Cameras[slot].VirtualCameraEnabled = running;
            }

            ConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Vcam slot {slot}: saving the choice failed {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the slots marked enabled in the config (per-camera flag plus the
    /// switcher flag). Blocking; callers run it on a worker thread.
    /// </summary>
    private bool StartEnabledVcams()
    {
        bool ok = _vcam.StartEnabled(
            _config.Cameras,
            i => i < _config.Cameras.Count
                ? _config.Cameras[i].VirtualCameraEnabled
                : _config.SwitcherVirtualCameraEnabled);

        AppLog.Write($"Vcam: enabled slots started -> {(ok ? "ok" : "failed")} " +
                     $"({CountRunning()} running)");
        return ok;
    }

    private int CountRunning()
    {
        int n = 0;
        for (int i = 0; i < _vcam.SlotCount; i++)
            if (_vcam.IsSlotRunning(i))
                n++;
        return n;
    }

    private int BusyCount(int slot) =>
        _vcamBusy.TryGetValue(slot, out int n) ? Volatile.Read(ref n) : 0;

    private void EnterBusy(int slot)
    {
        _vcamBusy.AddOrUpdate(slot, 1, (_, n) => n + 1);
    }

    private void ExitBusy(int slot)
    {
        _vcamBusy.AddOrUpdate(slot, 0, (_, n) => Math.Max(0, n - 1));
    }

    /// <summary>Raises <see cref="VcamStateChanged"/> on the UI thread.</summary>
    private void RaiseVcamStateChanged() => Raise(VcamStateChanged);

    private void RaiseVirtualCameraStateChanged() => Raise(VirtualCameraStateChanged);

    private void Raise(Action? handler)
    {
        if (handler == null)
            return;
        var context = _uiContext;
        if (context == null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            try { handler(); } catch (Exception ex) { AppLog.Write($"VcamStateChanged handler threw {ex.GetType().Name}: {ex.Message}"); }
            return;
        }
        context.Post(_ =>
        {
            try { handler(); } catch (Exception ex) { AppLog.Write($"VcamStateChanged handler threw {ex.GetType().Name}: {ex.Message}"); }
        }, null);
    }

    public async Task StartAsync(AppConfig config)
    {
        _config = config;
        _config.Normalize();
        SourceFormatFile.Write(_config.Width, _config.Height, _config.Fps);
        ApplyStartWithWindows(_config.StartWithWindows);

        // Virtual cameras come up from the per-slot flags saved last session, so
        // the switches the user left the app in are the ones they get back. The
        // native registration is slow, so run it off the UI thread (RebuildHotkeys
        // below must stay on the UI thread).
        await Task.Run(StartEnabledVcams);

        if (_config.CaptureEnabled)
            await RebuildPipelineAsync();
        else
            AppLog.Write("StartAsync: capture was off at last exit - cameras not started");

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
        else if (_engine != null)
        {
            // Delay is content only, so it is applied to the running engine
            // instead of rebuilding it: a rebuild would tear down and restart
            // every virtual camera, which other apps see as a device disconnect.
            ApplyDelays(_engine, _config);
        }

        if (formatChanged && _vcam.IsRunning)
        {
            // Restart the virtual cameras (off the UI thread) so they re-advertise
            // the new format. Done after the engine rebuild so the physical
            // cameras are already released. Only the slots that were running are
            // restarted; slots the user had turned off stay off.
            await Task.Run(() =>
            {
                int slots = _vcam.SlotCount;
                var wasRunning = new bool[slots];
                for (int i = 0; i < slots; i++)
                    wasRunning[i] = _vcam.IsSlotRunning(i);

                AppLog.Write("ApplyAsync: vcam Stop begin");
                _vcam.Stop();
                AppLog.Write("ApplyAsync: vcam Stop done");

                if (_vcam.Prepare(_config.Cameras))
                {
                    for (int i = 0; i < slots; i++)
                        if (wasRunning[i])
                            _vcam.StartSlot(i);
                }
                AppLog.Write("ApplyAsync: vcam Start done");
            });
            AppLog.Write("ApplyAsync: vcam restarted");
            RaiseVcamStateChanged();
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

    /// <summary>
    /// Global toggle ON: starts exactly the slots marked enabled (per-camera flag
    /// plus the switcher flag). Slots the user turned off individually stay off
    /// and are not registered - use <see cref="SetVcamRunningAsync"/> for those.
    /// Runs off the UI thread; completion raises the state events.
    /// </summary>
    /// <summary>
    /// Starts only the switcher virtual camera. Passthrough virtual cameras have
    /// their own per-camera switches, so this master never touches them.
    /// </summary>
    public void StartVirtualCamera()
    {
        AppLog.Write("StartVirtualCamera: switcher only");
        _ = SetSwitcherVcamRunningAsync(true);
    }

    /// <summary>Stops only the switcher virtual camera.</summary>
    public void StopVirtualCamera()
    {
        AppLog.Write("StopVirtualCamera: switcher only");
        _ = SetSwitcherVcamRunningAsync(false);
    }

    /// <summary>
    /// Turns live camera previews on or off. Off skips the per-frame preview
    /// downscale (a real CPU saving) while capture and every passthrough virtual
    /// camera keep running.
    /// </summary>
    public void SetPreviewsEnabled(bool enabled)
    {
        if (_config.PreviewsEnabled == enabled)
            return;

        _config.PreviewsEnabled = enabled;
        if (_engine != null)
            _engine.PreviewsEnabled = enabled;

        try { ConfigService.Save(_config); } catch (Exception ex) { AppLog.Write($"Previews: save failed {ex.Message}"); }

        AppLog.Write($"Previews: {(enabled ? "on" : "off")}");
        PreviewsStateChanged?.Invoke();
    }

    /// <summary>
    /// Tracks whether the window is on screen. Called from the window's
    /// StateChanged / IsVisibleChanged; the engine skips preview conversion while
    /// hidden, and resumes on the next frame after becoming visible again.
    /// </summary>
    public void SetPreviewsVisible(bool visible)
    {
        if (_previewsVisible == visible)
            return;
        _previewsVisible = visible;
        if (_engine != null)
            _engine.PreviewsVisible = visible;
    }

    public async Task StartCaptureAsync()
    {
        if (_engine != null)
            return;
        AppLog.Write("StartCapture");
        await RebuildPipelineAsync();

        SetCaptureEnabled(true);

        // Each hardware camera's virtual camera comes up with it instead of only
        // at app launch. Slots the user switched off individually stay off.
        if (!_vcam.IsRunning)
        {
            bool ok = await Task.Run(StartEnabledVcams);
            AppLog.Write($"StartCapture: virtual cameras auto-start {(ok ? "ok" : "failed")}");
            VirtualCameraStateChanged?.Invoke();
            VcamStateChanged?.Invoke();
        }

        RebuildHotkeys();
        SourcesChanged?.Invoke();
        CaptureStateChanged?.Invoke();
    }

    /// <summary>Records the capture switch so it survives a restart.</summary>
    private void SetCaptureEnabled(bool enabled)
    {
        if (_config.CaptureEnabled == enabled)
            return;
        _config.CaptureEnabled = enabled;
        try { ConfigService.Save(_config); }
        catch (Exception ex) { AppLog.Write($"CaptureEnabled: save failed {ex.Message}"); }
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
        SetCaptureEnabled(false);
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
        engine.PreviewsEnabled = _config.PreviewsEnabled;
        engine.PreviewsVisible = _previewsVisible;
        AppLog.Write($"Rebuild: engine StartAsync begin (previews={(_config.PreviewsEnabled ? "on" : "off")})");
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

    /// <summary>
    /// Pushes the configured delays into a running engine. Delay is deliberately
    /// absent from <see cref="CamerasEqual"/>: it is applied live, never by a
    /// rebuild, because a rebuild restarts every virtual camera.
    /// </summary>
    private static void ApplyDelays(CameraEngine engine, AppConfig config)
    {
        for (int i = 0; i < config.Cameras.Count; i++)
            engine.SetSlotDelay(i, config.Cameras[i].DelayMs);
        engine.SetCommonDelay(config.CommonDelayMs);
    }

    /// <summary>
    /// Pushes delays into the running engine at once, without saving the config and
    /// without rebuilding the pipeline, so a delay can be dialled in against a live
    /// picture. Callers must supply already-validated values; this never throws and
    /// does nothing when no engine is running. Deliberately independent of
    /// <see cref="ApplyAsync"/>: the settings dialog calls this on every keystroke,
    /// while Apply persists the value.
    /// </summary>
    public void ApplyDelaysLive(int commonDelayMs, IReadOnlyList<int> slotDelays)
    {
        var engine = _engine;
        if (engine == null)
            return;

        int slots = Math.Min(slotDelays.Count, engine.Sources.Count);
        for (int i = 0; i < slots; i++)
            engine.SetSlotDelay(i, slotDelays[i]);
        engine.SetCommonDelay(commonDelayMs);
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
