using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

// Verifies that the main "Virtual Camera" button owns only the switcher virtual
// camera, that each preview tile owns its own camera's passthrough switch, and
// that those choices persist.
//   args[0] = "switcher" (default) or "chip:<index>"
return Run(args);

static int Run(string[] args)
{
    string mode = args.Length > 0 ? args[0] : "switcher";

    var proc = Process.GetProcessesByName("WebcamSwitcher")
                      .OrderByDescending(p => p.StartTime)
                      .FirstOrDefault();
    if (proc == null) { Log("FATAL: WebcamSwitcher is not running"); return 2; }

    using var automation = new UIA3Automation();
    var app = Application.Attach(proc);
    var main = app.GetMainWindow(automation, TimeSpan.FromSeconds(20));
    if (main == null) { Log("FATAL: main window not found"); return 3; }

    // The window handle appears before its content is realised, so an early
    // attach finds an untitled shell with none of the controls. Wait for a
    // titled main window before looking for anything inside it.
    var titleDeadline = DateTime.UtcNow.AddSeconds(25);
    while (string.IsNullOrWhiteSpace(Name(main)) && DateTime.UtcNow < titleDeadline)
    {
        Thread.Sleep(400);
        var again = app.GetMainWindow(automation, TimeSpan.FromSeconds(5));
        if (again != null) main = again;
    }
    Log($"main window: '{Name(main)}'");

    var toggle = main.FindFirstDescendant(cf => cf.ByAutomationId("VcamToggle"));
    if (toggle == null) { Log("FATAL: VcamToggle not found"); return 4; }

    int count = CountChips(main);
    Log($"chips found: {count}");

    Log($"BEFORE  switcher='{Name(toggle)}'  chips=[{Chips(main, count)}]");

    if (mode.StartsWith("chip:", StringComparison.OrdinalIgnoreCase))
    {
        int index = int.Parse(mode.Substring(5));
        var chip = main.FindFirstDescendant(cf => cf.ByAutomationId($"VcamChip_{index}"));
        if (chip == null) { Log($"FATAL: VcamChip_{index} not found"); return 5; }

        chip.AsButton().Invoke();
        Log($"clicked VcamChip_{index}");
        Settle(main, toggle, count, 20000);

        Log($"AFTER   switcher='{Name(toggle)}'  chips=[{Chips(main, count)}]");
        DumpConfig();
        Log("RESULT: chip toggle done");
        return 0;
    }

    toggle.AsButton().Invoke();
    Log("clicked VcamToggle");
    Settle(main, toggle, count, 20000);

    string afterSwitcher = Name(toggle);
    string afterChips = Chips(main, count);
    Log($"AFTER   switcher='{afterSwitcher}'  chips=[{afterChips}]");

    DumpConfig();
    Log("RESULT: switcher toggle done");
    return 0;
}

static void Settle(Window main, AutomationElement toggle, int count, int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    string last = "";
    while (DateTime.UtcNow < deadline)
    {
        string now = Name(toggle) + "|" + Chips(main, count);
        if (now == last && !now.Contains("...") && !now.Contains("starting") && !now.Contains("stopping"))
        {
            Thread.Sleep(400);
            if (Name(toggle) + "|" + Chips(main, count) == last)
                return;
        }
        last = now;
        Thread.Sleep(400);
    }
    Log("(settle timed out)");
}

static int CountChips(Window main)
{
    int n = 0;
    while (main.FindFirstDescendant(cf => cf.ByAutomationId($"VcamChip_{n}")) != null && n < 16)
        n++;
    return n;
}

static string Chips(Window main, int count)
{
    var parts = new List<string>();
    for (int i = 0; i < count; i++)
    {
        var chip = main.FindFirstDescendant(cf => cf.ByAutomationId($"VcamChip_{i}"));
        parts.Add(chip == null ? $"{i}:?" : $"{i}:{State(Name(chip))}");
    }
    return string.Join(", ", parts);
}

static string State(string name)
{
    if (name.Contains("starting", StringComparison.OrdinalIgnoreCase)) return "starting";
    if (name.Contains("stopping", StringComparison.OrdinalIgnoreCase)) return "stopping";
    if (name.Contains("failed", StringComparison.OrdinalIgnoreCase)) return "failed";
    if (name.Contains("on", StringComparison.OrdinalIgnoreCase)) return "ON";
    return "off";
}

static string Name(AutomationElement e)
{
    try { return e.Properties.Name.ValueOrDefault ?? ""; } catch { return "?"; }
}

static void DumpConfig()
{
    try
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WebcamSwitcher", "config.json");
        if (!File.Exists(path)) { Log($"config: {path} missing"); return; }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        bool switcher = root.TryGetProperty("SwitcherVirtualCameraEnabled", out var s) && s.GetBoolean();
        bool capture = !root.TryGetProperty("CaptureEnabled", out var c) || c.GetBoolean();
        bool previews = !root.TryGetProperty("PreviewsEnabled", out var p) || p.GetBoolean();
        int active = root.TryGetProperty("ActiveIndex", out var a) ? a.GetInt32() : -1;

        Log($"config: switcher={switcher} capture={capture} previews={previews} activeIndex={active}");

        if (root.TryGetProperty("Cameras", out var cams))
        {
            int i = 0;
            foreach (var cam in cams.EnumerateArray())
            {
                string name = cam.TryGetProperty("FriendlyName", out var f) ? f.GetString() ?? "?" : "?";
                bool on = !cam.TryGetProperty("VirtualCameraEnabled", out var v) || v.GetBoolean();
                Log($"config: camera[{i}] '{name}' passthroughVcam={on}");
                i++;
            }
        }
    }
    catch (Exception ex) { Log($"config read failed: {ex.Message}"); }
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
