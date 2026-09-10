using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;

namespace WebcamSwitcher.FormatApplyTest;

internal static class Program
{
    const string Clsid = "{7B1C4D2E-8F3A-4B5C-9D6E-1F2A3B4C5D6E}";
    const string Name = "FormatApply Vcam";

    static string FormatFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WebcamSwitcher", "format.ini");

    [DllImport("WebcamSwitcher.Source.dll", CharSet = CharSet.Unicode)]
    static extern int WebcamSwitcher_Start([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string clsid, out IntPtr handle);
    [DllImport("WebcamSwitcher.Source.dll")]
    static extern void WebcamSwitcher_Stop(IntPtr handle);

    static void WriteFormat(int w, int h, int fps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FormatFile)!);
        File.WriteAllText(FormatFile, $"width={w}\nheight={h}\nfps={fps}\n");
    }

    static async Task<(int w, int h, int fps)?> ReadAdvertised()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        var dev = devices.FirstOrDefault(d => d.Name.Contains(Name, StringComparison.OrdinalIgnoreCase));
        if (dev == null)
            return null;
        using var mc = new MediaCapture();
        await mc.InitializeAsync(new MediaCaptureInitializationSettings { VideoDeviceId = dev.Id });
        var props = mc.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
        return ((int)props!.Width, (int)props.Height, (int)props.FrameRate.Numerator);
    }

    static async Task<int> Main()
    {
        Console.WriteLine("=== FormatApply test ===");

        // Phase 1: 1280x720@30
        WriteFormat(1280, 720, 30);
        WebcamSwitcher_Start(Name, Clsid, out IntPtr h1);
        await Task.Delay(1200);
        var r1 = await ReadAdvertised();
        Console.WriteLine($"phase1 advertised (expected 1280x720@30): {r1?.w}x{r1?.h}@{r1?.fps}");

        // Phase 2: stop, rewrite format, restart (simulates ApplyAsync format change)
        WebcamSwitcher_Stop(h1);
        WriteFormat(1920, 1080, 30);
        WebcamSwitcher_Start(Name, Clsid, out IntPtr h2);
        await Task.Delay(1200);
        var r2 = await ReadAdvertised();
        Console.WriteLine($"phase2 advertised (expected 1920x1080@30): {r2?.w}x{r2?.h}@{r2?.fps}");

        bool ok = r1 is { w: 1280, h: 720, fps: 30 } && r2 is { w: 1920, h: 1080, fps: 30 };
        Console.WriteLine(ok ? "RESULT: OK (format change applies via vcam restart)" : "RESULT: FAIL");

        WebcamSwitcher_Stop(h2);
        Console.WriteLine("=== DONE ===");
        return ok ? 0 : 1;
    }
}
