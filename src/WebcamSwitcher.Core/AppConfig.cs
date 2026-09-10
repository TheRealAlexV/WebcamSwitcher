namespace WebcamSwitcher.Core;

public class CameraConfig
{
    public string? DeviceId { get; set; }
    public string FriendlyName { get; set; } = "";
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
