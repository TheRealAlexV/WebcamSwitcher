namespace WebcamSwitcher.Core;

public class CameraConfig
{
    public string? DeviceId { get; set; }
    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// Optional per-camera capture size. When null, the closest supported format
    /// whose aspect matches the global output is chosen automatically (which keeps
    /// e.g. a 4:3 webcam from being pillarboxed into a 16:9 canvas).
    /// </summary>
    public int? CaptureWidth { get; set; }
    public int? CaptureHeight { get; set; }

    /// <summary>
    /// Optional capture-format preference: "Auto" (default), "Nv12" or "Mjpg".
    /// Auto keeps the uncompressed stream when it already runs at full rate and
    /// otherwise switches to MJPG, which is the only way a USB 2.0 camera reaches
    /// 30 fps at 720p/1080p.
    /// </summary>
    public string CaptureFormat { get; set; } = "Auto";

    /// <summary>
    /// Whether this camera's passthrough virtual camera is created at launch and
    /// by the global virtual-camera toggle. Defaults to true; turning it off
    /// stops exactly this slot and leaves the others running. The friendly name
    /// and pipe of every slot are computed regardless, so this slot can be
    /// started later without a restart and never renames its neighbours.
    /// </summary>
    public bool VirtualCameraEnabled { get; set; } = true;

    /// <summary>
    /// Extra video delay for this camera, in milliseconds (default 0, i.e. no
    /// delay at all). Added to <see cref="AppConfig.CommonDelayMs"/> before it is
    /// converted to frames, so a camera that is consistently ahead of the others
    /// can be lined up with them. 0 costs nothing: the delay stage is bypassed.
    /// </summary>
    public int DelayMs { get; set; }
}

public class AppConfig
{
    public int Width { get; set; } = Protocol.DefaultWidth;
    public int Height { get; set; } = Protocol.DefaultHeight;
    public int Fps { get; set; } = Protocol.DefaultFps;
    public List<CameraConfig> Cameras { get; set; } = new();
    public int ActiveIndex { get; set; } = 0;

    public string[]? Hotkeys { get; set; }
    public string? RotateHotkey { get; set; }

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Legacy master switch, kept for config compatibility only. Virtual cameras
    /// now start from the per-slot flags (<see cref="SwitcherVirtualCameraEnabled"/>
    /// and <see cref="CameraConfig.VirtualCameraEnabled"/>), so this value is no
    /// longer consulted anywhere.
    /// </summary>
    public bool VirtualCameraOnLaunch { get; set; }

    /// <summary>
    /// Whether the switcher virtual camera ("WebcamSwitcher Virtual Camera") is
    /// created at launch and by the global toggle. Defaults to true and is
    /// independent of the per-camera <see cref="CameraConfig.VirtualCameraEnabled"/>.
    /// </summary>
    public bool SwitcherVirtualCameraEnabled { get; set; } = true;

    /// <summary>
    /// Whether live camera thumbnails are rendered. Turning this off skips the
    /// per-frame preview scaling (a measurable CPU saving) while capture and the
    /// passthrough virtual cameras keep running.
    /// </summary>
    public bool PreviewsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the cameras are capturing. Persisted so quitting with capture off
    /// does not silently start the cameras again on the next launch.
    /// </summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>
    /// When on, each camera's exposure is pinned to manual 1/30 s so a dim scene
    /// cannot make the camera lengthen integration time and drop to 15 fps. The
    /// image may be darker in low light; that is the accepted tradeoff.
    /// </summary>
    public bool PreferSmooth30Fps { get; set; }

    /// <summary>
    /// Video delay applied to every camera, in milliseconds (default 0, i.e. no
    /// delay at all). Each camera adds its own <see cref="CameraConfig.DelayMs"/>
    /// to this, so the common value sets the baseline latency and the per-camera
    /// value lines up the odd camera out. 0 bypasses the delay stage entirely.
    /// </summary>
    public int CommonDelayMs { get; set; }

    /// <summary>Largest accepted delay; 2 s is far more than A/V sync needs.</summary>
    public const int MaxDelayMs = 2000;

    /// <summary>Clamps/repairs values so they are always valid before use.</summary>
    public void Normalize()
    {
        Width = ClampEven(Width, Protocol.DefaultWidth);
        Height = ClampEven(Height, Protocol.DefaultHeight);
        Fps = Math.Clamp(Fps, 1, 60);

        CommonDelayMs = Math.Clamp(CommonDelayMs, 0, MaxDelayMs);
        foreach (var camera in Cameras)
            camera.DelayMs = Math.Clamp(camera.DelayMs, 0, MaxDelayMs);

        int count = Cameras.Count;
        if (Hotkeys == null)
            Hotkeys = new string[count];
        else if (Hotkeys.Length != count)
        {
            var arr = new string[count];
            Array.Copy(Hotkeys, arr, Math.Min(Hotkeys.Length, count));
            Hotkeys = arr;
        }

        ActiveIndex = Math.Clamp(ActiveIndex, 0, Math.Max(0, count - 1));

        // The two virtual-camera switches are plain booleans with a "keep it
        // stable" contract: Normalize never rewrites them, so a slot the user
        // turned off stays off across saves, reloads and launches.
    }

    private static int ClampEven(int v, int def)
    {
        if (v < 160 || v > 3840)
            v = def;
        return v & ~1;
    }
}
