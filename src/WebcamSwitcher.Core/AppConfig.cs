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

    // Hotkeys (M3)
    public string[]? Hotkeys { get; set; }
    public string? RotateHotkey { get; set; }
}
