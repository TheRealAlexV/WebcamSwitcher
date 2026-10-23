using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

// Verifies the video-delay settings end to end against an already-running app.
//   args[0] = output dir for screenshots
//   args[1] = "apply" to click Apply (persist), anything else leaves it unsaved
//   args[2] = value for camera 0's own delay
// Deliberately attaches instead of launching: the live-update claim only means
// something against a pipeline that is already streaming.
return Run(args);

static int Run(string[] args)
{
    string outDir = args.Length > 0 ? args[0] : @"C:\Dev\WebcamSwitcher\WebcamSwitcher\artifacts\ui";
    bool apply = args.Length > 1 && args[1].Equals("apply", StringComparison.OrdinalIgnoreCase);
    string cam0Value = args.Length > 2 ? args[2] : "40";
    Directory.CreateDirectory(outDir);
    Log($"outDir={outDir} apply={apply} cam0={cam0Value}");

    var proc = Process.GetProcessesByName("WebcamSwitcher")
                      .OrderByDescending(p => p.StartTime)
                      .FirstOrDefault();
    if (proc == null) { Log("FATAL: WebcamSwitcher is not running"); return 2; }
    // Capture the ids now: FlaUI's Application takes ownership of the Process and
    // invalidates it, so touching proc afterwards throws "No process is associated".
    int pid = proc.Id;
    int sessionId = proc.SessionId;
    Log($"attached pid={pid} session={sessionId}");

    using var automation = new UIA3Automation();
    var app = Application.Attach(proc);

    Window? main = null;
    try { main = app.GetMainWindow(automation, TimeSpan.FromSeconds(20)); }
    catch (Exception ex) { Log($"FATAL: main window: {ex.Message}"); return 3; }
    if (main == null) { Log("FATAL: main window not found"); return 3; }
    Log($"main window: '{main.Title}'");

    // A previous aborted run may have left the modal dialog open; reuse it rather
    // than clicking a button that is now disabled behind the modal.
    Window? settings = FindSettings(app, automation, pid, quiet: true);
    if (settings != null)
    {
        Log("settings window was already open; reusing it");
    }
    else
    {
        Log($"main.IsEnabled={Safe(() => main.IsEnabled.ToString())}");

        // Control test: if Invoke works at all, the preview toggle's label flips.
        var prev = main.FindFirstDescendant(cf => cf.ByAutomationId("PreviewsToggle"));
        if (prev != null)
        {
            string before = Safe(() => prev.Name);
            try { prev.AsButton().Invoke(); } catch (Exception ex) { Log($"previews invoke failed: {ex.Message}"); }
            Thread.Sleep(1200);
            string after = Safe(() => prev.Name);
            Log($"INVOKE-CONTROL: previews '{before}' -> '{after}' ({(before != after ? "Invoke works" : "INVOKE DID NOTHING")})");
            try { prev.AsButton().Invoke(); } catch { }
            Thread.Sleep(800);
        }

        var settingsBtn = main.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"));
        if (settingsBtn == null) { Log("FATAL: SettingsButton not found"); return 4; }
        Log($"settings button enabled={Safe(() => settingsBtn.IsEnabled.ToString())} offscreen={Safe(() => settingsBtn.IsOffscreen.ToString())}");

        try { settingsBtn.AsButton().Invoke(); } catch (Exception ex) { Log($"invoke threw: {ex.Message}"); }
        Log("invoked Settings");
        settings = FindSettings(app, automation, pid, quiet: true);

        if (settings == null)
        {
            // Fall back to a real mouse click in case the template ignores Invoke.
            try { main.SetForeground(); } catch { }
            Thread.Sleep(400);
            try { settingsBtn.Click(); Log("mouse-clicked Settings"); } catch (Exception ex) { Log($"mouse click failed: {ex.Message}"); }
            settings = FindSettings(app, automation, pid);
        }

        if (settings == null) { DumpWindows(app, automation, pid); Log("FATAL: Settings window not found"); return 5; }
    }

    Log($"settings window: '{settings.Title}'");
    Thread.Sleep(1200);

    var common = settings.FindFirstDescendant(cf => cf.ByAutomationId("CommonDelayBox"));
    var d0 = settings.FindFirstDescendant(cf => cf.ByAutomationId("DelayBox0"));
    var d1 = settings.FindFirstDescendant(cf => cf.ByAutomationId("DelayBox1"));

    Log($"CommonDelayBox: {(common != null ? "FOUND" : "MISSING")}");
    Log($"DelayBox0 (per-camera 0): {(d0 != null ? "FOUND" : "MISSING")}");
    Log($"DelayBox1 (per-camera 1): {(d1 != null ? "FOUND" : "MISSING")}");
    if (common == null || d0 == null) { Log("RESULT: FAIL (delay controls missing)"); return 6; }

    Log($"initial values: common='{common.AsTextBox().Text}' cam0='{d0.AsTextBox().Text}' cam1='{d1?.AsTextBox().Text}'");

    // Type values and deliberately do NOT click Apply: the live path must reach
    // the running pipeline with no rebuild and no save.
    common.AsTextBox().Text = "79";
    Thread.Sleep(800);
    Log($"typed common=79 -> common='{common.AsTextBox().Text}'");

    d0.AsTextBox().Text = cam0Value;
    Thread.Sleep(800);
    Log($"typed cam0={cam0Value} -> cam0='{d0.AsTextBox().Text}'");

    if (d1 != null) { d1.AsTextBox().Text = "0"; Thread.Sleep(400); }

    Thread.Sleep(1500);
    SaveShot(settings, Path.Combine(outDir, "settings-delay.png"));
    Log($"before Apply: common='{common.AsTextBox().Text}' cam0='{d0.AsTextBox().Text}'");

    if (!apply)
    {
        var cancel = settings.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"));
        if (cancel != null) { cancel.AsButton().Invoke(); Log("clicked Cancel (values intentionally not saved)"); }
        Thread.Sleep(800);
        Log("RESULT: OK (live values set without Apply)");
        return 0;
    }

    var applyBtn = settings.FindFirstDescendant(cf => cf.ByAutomationId("ApplyButton"));
    if (applyBtn == null) { Log("FATAL: ApplyButton not found"); return 7; }
    var sw = Stopwatch.StartNew();
    applyBtn.AsButton().Invoke();
    Log("clicked Apply");

    bool closed = false;
    var applyDeadline = DateTime.UtcNow.AddSeconds(30);
    while (DateTime.UtcNow < applyDeadline)
    {
        if (FindSettings(app, automation, pid, quiet: true) == null) { closed = true; break; }
        Thread.Sleep(400);
    }
    sw.Stop();
    Log($"Apply took {sw.ElapsedMilliseconds} ms, closed={closed}");
    Log(closed ? "RESULT: OK (delay saved)" : "RESULT: FAIL (dialog did not close)");
    return closed ? 0 : 8;
}

static Window? FindSettings(Application app, UIA3Automation automation, int pid, bool quiet = false)
{
    var deadline = DateTime.UtcNow.AddSeconds(quiet ? 0 : 20);
    do
    {
        // HWND first. The Settings window is a modal dialog owned by the main
        // window, and FlaUI's desktop-children walk does not always surface it.
        try
        {
            IntPtr hwnd = Win32.FindVisibleWindow(pid, "Settings");
            if (hwnd != IntPtr.Zero)
            {
                var element = automation.FromHandle(hwnd);
                if (element != null)
                    return element.AsWindow();
            }
        }
        catch (Exception ex) { if (!quiet) Log($"hwnd lookup failed: {ex.Message}"); }

        // Fall back to FlaUI's own top-level enumeration.
        try
        {
            foreach (var w in app.GetAllTopLevelWindows(automation))
            {
                string n;
                try { n = w.Properties.Name.ValueOrDefault ?? ""; } catch { continue; }
                if (n.Contains("Settings", StringComparison.OrdinalIgnoreCase))
                    return w;
            }
        }
        catch (Exception ex) { if (!quiet) Log($"window enumeration failed: {ex.Message}"); }

        if (quiet) break;
        Thread.Sleep(500);
    } while (DateTime.UtcNow < deadline);

    return null;
}

static void DumpWindows(Application app, UIA3Automation automation, int pid)
{
    Win32.DumpPid(pid, Log);
    try
    {
        var all = app.GetAllTopLevelWindows(automation);
        Log($"dump: {all.Length} top-level window(s) for pid {pid}");
        foreach (var w in all)
        {
            string n, cn;
            try { n = w.Properties.Name.ValueOrDefault ?? ""; } catch { n = "?"; }
            try { cn = w.Properties.ClassName.ValueOrDefault ?? ""; } catch { cn = "?"; }
            Log($"  name='{n}' class='{cn}'");
        }
    }
    catch (Exception ex) { Log($"dump failed: {ex.Message}"); }
}

static void SaveShot(Window window, string path)
{
    try { using var bmp = window.Capture(); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); Log($"shot: {path}"); }
    catch (Exception ex) { Log($"shot failed: {ex.Message}"); }
}

static string Safe(Func<string> read)
{
    try { return read(); } catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

/// <summary>
/// Enumerates every top-level window owned by a process through Win32, including
/// ones UIA does not surface. Needed to tell "no dialog was created" apart from
/// "a dialog exists but is not visible or not enumerable via UIA".
/// </summary>
static class Win32
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int max);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int max);

    /// <summary>First visible top-level window of <paramref name="pid"/> whose title contains the fragment.</summary>
    public static IntPtr FindVisibleWindow(int pid, string titleFragment)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != (uint)pid)
                return true;
            if (!IsWindowVisible(hWnd))
                return true;

            var title = new System.Text.StringBuilder(512);
            GetWindowTextW(hWnd, title, title.Capacity);
            if (title.ToString().Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void DumpPid(int pid, Action<string> log)
    {
        int count = 0;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            if (owner != (uint)pid)
                return true;

            var title = new System.Text.StringBuilder(256);
            GetWindowTextW(hWnd, title, title.Capacity);
            var cls = new System.Text.StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);

            count++;
            log($"  win32 hwnd=0x{hWnd.ToInt64():X} visible={IsWindowVisible(hWnd)} class='{cls}' title='{title}'");
            return true;
        }, IntPtr.Zero);
        log($"win32: {count} top-level window(s) for pid {pid}");
    }
}
