using WebcamSwitcher.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

async Task<(int w, int h, string fmt)?> ReadVcamFrame(string nameContains)
{
    var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
    var dev = devices.FirstOrDefault(d => d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    if (dev == null) return null;

    var groups = await MediaFrameSourceGroup.FindAllAsync();
    var group = groups.FirstOrDefault(g => g.Id == dev.Id);
    if (group == null) return null;

    var capture = new MediaCapture();
    await capture.InitializeAsync(new MediaCaptureInitializationSettings
    {
        SourceGroup = group,
        SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu
    });

    var color = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
    if (color == null) { capture.Dispose(); return null; }

    var reader = await capture.CreateFrameReaderAsync(color, MediaEncodingSubtypes.Nv12);
    var tcs = new TaskCompletionSource<(int, int, string)>();
    reader.FrameArrived += (s, e) =>
    {
        using var f = s.TryAcquireLatestFrame();
        var sw = f?.VideoMediaFrame?.SoftwareBitmap;
        if (sw != null && !tcs.Task.IsCompleted)
            tcs.TrySetResult((sw.PixelWidth, sw.PixelHeight, sw.BitmapPixelFormat.ToString()));
    };
    await reader.StartAsync();
    try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
    finally { await reader.StopAsync(); capture.Dispose(); }
}

try
{
    Console.WriteLine("=== WebcamSwitcher CaptureTest ===");

    using var vcam = new VirtualCameraService("CaptureTest Vcam");
    Console.WriteLine($"VirtualCameraService.Start = {vcam.Start()}");

    var cams = await CameraEngine.EnumerateCamerasAsync();
    Console.WriteLine("Cameras:");
    foreach (var c in cams) Console.WriteLine($"  - {c.FriendlyName}");

    var camA = cams.FirstOrDefault(c => c.FriendlyName.Contains("C920", StringComparison.OrdinalIgnoreCase));
    var camB = cams.FirstOrDefault(c => c.FriendlyName.Contains("OsmoPocket3", StringComparison.OrdinalIgnoreCase));
    if (camA == null || camB == null)
    {
        Console.WriteLine("RESULT: required cameras not found");
        return 1;
    }

    var config = new AppConfig
    {
        Width = 1920, Height = 1080, Fps = 30,
        Cameras = new List<CameraConfig> { camA, camB },
        ActiveIndex = 0
    };

    var publisher = new FramePublisher(config.Width, config.Height, config.Fps);
    var engine = new CameraEngine(publisher);
    int started = await engine.StartAsync(config);
    Console.WriteLine($"Started {started} camera sources");

    await Task.Delay(3000);

    var f1 = await ReadVcamFrame("CaptureTest Vcam");
    Console.WriteLine($"Frame 1 (active=0 {camA.FriendlyName}): {f1?.w}x{f1?.h} {f1?.fmt}");
    Console.WriteLine(f1 is { w: 1920, h: 1080 } ? "FRAME1: OK (1920x1080)" : "FRAME1: MISMATCH");

    engine.ActiveIndex = 1;
    await Task.Delay(1000);
    var f2 = await ReadVcamFrame("CaptureTest Vcam");
    Console.WriteLine($"Frame 2 (active=1 {camB.FriendlyName}): {f2?.w}x{f2?.h} {f2?.fmt}");
    Console.WriteLine(f2 is { w: 1920, h: 1080 } ? "FRAME2: OK (1920x1080)" : "FRAME2: MISMATCH");

    Console.WriteLine($"Clients connected: {publisher.ClientCount}");
    Console.WriteLine(f1 is { w: 1920 } && f2 is { w: 1920 } ? "RESULT: OK" : "RESULT: FAIL");

    await engine.DisposeAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"RESULT: ERROR {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.ToString());
}

Console.WriteLine("=== DONE ===");
return 0;
