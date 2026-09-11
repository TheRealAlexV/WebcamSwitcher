using WebcamSwitcher.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

// Opens the virtual camera whose friendly name contains `nameContains` via a real
// Media Foundation consumer (MediaCapture), reads one NV12 frame, and invokes
// `onOpen` while the vcam is still held open (so pipe client counts can be sampled).
async Task<(int w, int h, string fmt)?> ProbeVcam(string nameContains, Action? onOpen = null)
{
    var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
    var dev = devices.FirstOrDefault(d => d.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    if (dev == null) { Console.WriteLine($"  [probe] device not found: {nameContains}"); return null; }

    var groups = await MediaFrameSourceGroup.FindAllAsync();
    var group = groups.FirstOrDefault(g => g.Id == dev.Id);
    if (group == null) { Console.WriteLine($"  [probe] no frame source group for {nameContains}"); return null; }

    var capture = new MediaCapture();
    try
    {
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });

        var color = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (color == null) { Console.WriteLine("  [probe] no color frame source"); return null; }

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
        try
        {
            await Task.Delay(1000);      // let the native source connect to its pipe
            onOpen?.Invoke();
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { await reader.StopAsync(); }
    }
    finally { capture.Dispose(); }
}

// Consume-only mode: read one frame from an existing (app-created) virtual camera.
if (args.Length >= 2 && args[0] == "consume")
{
    var r = await ProbeVcam(args[1]);
    Console.WriteLine($"CONSUME '{args[1]}' -> {(r == null ? "NO FRAME" : $"{r.Value.w}x{r.Value.h} {r.Value.fmt}")}");
    return r != null ? 0 : 1;
}

Console.WriteLine("=== WebcamSwitcher Phase2 CaptureTest ===");

var cams = await CameraEngine.EnumerateCamerasAsync();
Console.WriteLine("Cameras:");
foreach (var c in cams) Console.WriteLine($"  - {c.FriendlyName}");

var camA = cams.FirstOrDefault(c => c.FriendlyName.Contains("C920", StringComparison.OrdinalIgnoreCase));
var camB = cams.FirstOrDefault(c => c.FriendlyName.Contains("OsmoPocket3", StringComparison.OrdinalIgnoreCase));
if (camA == null || camB == null) { Console.WriteLine("RESULT: required cameras not found"); return 1; }

var config = new AppConfig
{
    Width = 1280, Height = 720, Fps = 30,
    Cameras = new List<CameraConfig> { camA, camB },
    ActiveIndex = 0
};
// The virtual camera advertises this format in its stream descriptor, so the
// harness must write it (the app normally does this during rebuild).
SourceFormatFile.Write(config.Width, config.Height, config.Fps);

// N passthrough publishers (index-aligned with config.Cameras) + the switcher.
var publishers = new List<FramePublisher>();
for (int i = 0; i < config.Cameras.Count; i++)
    publishers.Add(new FramePublisher(Protocol.PassthroughPipeName(i), config.Width, config.Height, config.Fps));
var switcher = new FramePublisher(Protocol.PipeName, config.Width, config.Height, config.Fps);

var engine = new CameraEngine(publishers, switcher);
int started = await engine.StartAsync(config);
Console.WriteLine($"Started {started} camera sources");

using var vcam = new VirtualCameraService();
bool vcamOk = vcam.Start(config.Cameras);
Console.WriteLine($"VirtualCameraService.Start = {vcamOk}");

var iniPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebcamSwitcher", "vcams.ini");
Console.WriteLine("--- vcams.ini ---");
Console.WriteLine(File.Exists(iniPath) ? File.ReadAllText(iniPath).Replace("\t", "  <TAB>  ") : "MISSING");
Console.WriteLine("-----------------");

await Task.Delay(3000);

string nameA = $"{camA.FriendlyName} (WebcamSwitcher)";
string nameB = $"{camB.FriendlyName} (WebcamSwitcher)";

// Make camera B the ACTIVE camera first. If routing were broken (a passthrough
// falling back to the switcher pipe), the "A" vcam would then be served by B.
engine.ActiveIndex = 1;
await Task.Delay(700);
Console.WriteLine($"Active camera now B = '{camB.FriendlyName}'");

// Regression: the now-INACTIVE camera A must keep feeding its own passthrough
// vcam (p0). Previously FrameReady was gated on the active camera, so switching
// away left the other vcam dark.
long inactiveBefore = publishers[0].PublishCount;
await Task.Delay(2500);
long inactiveAfter = publishers[0].PublishCount;
Console.WriteLine($"INACTIVE-CAMERA-PUBLISH: p0 frames {inactiveBefore} -> {inactiveAfter} (+{inactiveAfter - inactiveBefore}) in 2.5s");
Console.WriteLine((inactiveAfter > inactiveBefore) ? "INACTIVE-CAMERA-PUBLISH: OK (inactive camera keeps streaming to its vcam)" : "INACTIVE-CAMERA-PUBLISH: FAIL (inactive camera stopped publishing)");

int cA = -1;
var fa = await ProbeVcam(nameA, () => cA = publishers[0].ClientCount);
Console.WriteLine($"Frame A ('{nameA}'): {fa?.w}x{fa?.h} {fa?.fmt}");
Console.WriteLine($"ROUTING A: p0.ClientCount(open)={cA} p1.ClientCount={publishers[1].ClientCount} switcher.ClientCount={switcher.ClientCount}");
Console.WriteLine((fa is { w: 1280, h: 720 } && cA > 0) ? "PASSTHROUGH-A: OK (bound to p0)" : "PASSTHROUGH-A: FAIL");

int cB = -1;
var fb = await ProbeVcam(nameB, () => cB = publishers[1].ClientCount);
Console.WriteLine($"Frame B ('{nameB}'): {fb?.w}x{fb?.h} {fb?.fmt}");
Console.WriteLine((fb is { w: 1280, h: 720 } && cB > 0) ? "PASSTHROUGH-B: OK (bound to p1)" : "PASSTHROUGH-B: FAIL");

int cS = -1;
var fs = await ProbeVcam(VirtualCameraService.SwitcherName, () => cS = switcher.ClientCount);
Console.WriteLine($"Frame S (switcher): {fs?.w}x{fs?.h} {fs?.fmt}");
Console.WriteLine((fs is { w: 1280, h: 720 } && cS > 0) ? "SWITCHER: OK (bound to switcher pipe)" : "SWITCHER: FAIL");
// Each registered vcam's source connects to its own pipe, so all three pipes
// must have exactly one client. If name->pipe resolution had failed, every vcam
// would fall back to the switcher pipe (switcher=3, p0=p1=0).
Console.WriteLine((cA > 0 && cB > 0 && cS > 0) ? "ROUTING: OK (each vcam bound to a distinct pipe)" : "ROUTING: FAIL (fell back / collapsed onto one pipe)");

await engine.DisposeAsync();
foreach (var p in publishers) p.Dispose();
switcher.Dispose();

Console.WriteLine("RESULT: DONE");
return 0;
