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

    /// <summary>Whether the virtual camera is registered/enabled at app launch.</summary>
    public bool VirtualCameraOnLaunch { get; set; }

    /// <summary>
    /// Whether live camera thumbnails are rendered. Turning this off skips the
    /// per-frame preview scaling (a measurable CPU saving) while capture and the
    /// passthrough virtual cameras keep running.
    /// </summary>
    public bool PreviewsEnabled { get; set; } = true;

    /// <summary>
    /// When on, each camera's exposure is pinned to manual 1/30 s so a dim scene
    /// cannot make the camera lengthen integration time and drop to 15 fps. The
    /// image may be darker in low light; that is the accepted tradeoff.
    /// </summary>
    public bool PreferSmooth30Fps { get; set; }

    /// <summary>Clamps/repairs values so they are always valid before use.</summary>
    public void Normalize()
    {
        Width = ClampEven(Width, Protocol.DefaultWidth);
        Height = ClampEven(Height, Protocol.DefaultHeight);
        Fps = Math.Clamp(Fps, 1, 60);

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
    }

    private static int ClampEven(int v, int def)
    {
        if (v < 160 || v > 3840)
            v = def;
        return v & ~1;
    }
}
