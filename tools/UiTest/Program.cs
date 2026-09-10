using System.Diagnostics;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

return await Run(args);

static async Task<int> Run(string[] args)
{
    string appPath = args.Length > 0 ? args[0] : @"C:\src\WebcamSwitcher\artifacts\app\WebcamSwitcher.exe";
    string outDir = args.Length > 1 ? args[1] : @"C:\src\WebcamSwitcher\artifacts\ui";
    Directory.CreateDirectory(outDir);
    Log($"appPath={appPath}");
    Log($"outDir={outDir}");

    using var automation = new UIA3Automation();

    foreach (var p in Process.GetProcessesByName("WebcamSwitcher"))
        try { p.Kill(); p.WaitForExit(2000); } catch { }

    var app = Application.Launch(appPath);
    Window? main = null;
    try { main = app.GetMainWindow(automation, TimeSpan.FromSeconds(30)); }
    catch (Exception ex) { Log($"FATAL: main window not found: {ex.Message}"); return 2; }
    Log($"main window: '{main.Title}'");
    SaveShot(main, Path.Combine(outDir, "main.png"));

    var settingsBtn = main.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"));
    if (settingsBtn == null) { Log("FATAL: SettingsButton not found"); return 3; }
    settingsBtn.AsButton().Invoke();
    Log("clicked Settings");

    Window? settings = null;
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (settings == null && DateTime.UtcNow < deadline)
    {
        settings = FindSettingsWindow(automation);
        if (settings == null) await Task.Delay(500);
    }
    if (settings == null)
    {
        Log("FATAL: Settings window not found after open. Dumping all windows:");
        DumpWindows(automation);
        return 4;
    }
    Log($"settings window: '{settings.Title}'");
    await Task.Delay(1500);
    SaveShot(settings, Path.Combine(outDir, "settings.png"));

    var combos = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox));
    Log($"found {combos.Length} combo boxes");
    bool displayOk = true;
    int cameraComboCount = 0;
    foreach (var c in combos)
    {
        string id = c.Properties.AutomationId.ValueOrDefault ?? "";
        string value = GetComboText(c);
        Log($"  combo id='{id}' value='{value}'");
        if (id.StartsWith("CameraCombo"))
        {
            cameraComboCount++;
            if (string.IsNullOrEmpty(value) || value.Contains("CameraConfig"))
                displayOk = false;
        }
    }
    Log(displayOk && cameraComboCount >= 1
        ? "DISPLAY: OK (camera combos show real names)"
        : "DISPLAY: FAIL (camera combo shows type name or empty)");

    var combo0 = combos.FirstOrDefault(c => (c.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("CameraCombo"));
    if (combo0 != null)
    {
        try
        {
            var cb = combo0.AsComboBox();
            var items = cb.Items;
            Log($"CameraCombo0 has {items.Length} items");
            if (items.Length >= 2)
            {
                cb.Select(1);
                Log($"selected index 1 -> '{GetComboText(combo0)}'");
            }
        }
        catch (Exception ex) { Log($"select failed: {ex.Message}"); }
        await Task.Delay(500);
    }

    // Exercise a format change (resolution) too, to cover the vcam-restart path.
    SetComboText(settings, "WidthCombo", "1920");
    SetComboText(settings, "HeightCombo", "1080");
    SetComboText(settings, "FpsCombo", "30");
    await Task.Delay(300);

    var applyBtn = settings.FindFirstDescendant(cf => cf.ByAutomationId("ApplyButton"));
    if (applyBtn == null) { Log("FATAL: ApplyButton not found"); return 5; }
    var sw = Stopwatch.StartNew();
    applyBtn.AsButton().Invoke();
    Log("clicked Apply; waiting for dialog to close...");

    bool closed = false;
    var applyDeadline = DateTime.UtcNow.AddSeconds(25);
    while (DateTime.UtcNow < applyDeadline)
    {
        if (FindSettingsWindow(automation) == null) { closed = true; break; }
        await Task.Delay(500);
    }
    sw.Stop();
    Log($"Apply took {sw.ElapsedMilliseconds} ms");
    Log(closed ? "APPLY: OK (dialog closed within 25s)" : "APPLY: HUNG (dialog still open after 25s)");
    if (!closed)
        SaveShot(FindSettingsWindow(automation), Path.Combine(outDir, "settings-hung.png"));

    try { SaveShot(FindWindowByTitle(automation, "WebcamSwitcher"), Path.Combine(outDir, "main-after.png")); }
    catch { }

    try { app.Close(); } catch { }
    return (displayOk && closed) ? 0 : 1;
}

static Window? FindWindowByTitle(UIA3Automation automation, string title)
{
    var desktop = automation.GetDesktop();
    foreach (var w in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
    {
        if (string.Equals(SafeName(w), title, StringComparison.OrdinalIgnoreCase))
            return w.AsWindow();
    }
    return null;
}

static string SafeName(AutomationElement el)
{
    try { return el.Properties.Name.ValueOrDefault ?? ""; }
    catch { return ""; }
}

static Window? FindSettingsWindow(UIA3Automation automation)
{
    var desktop = automation.GetDesktop();
    foreach (var w in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
    {
        try
        {
            if (SafeName(w).Contains("Settings", StringComparison.OrdinalIgnoreCase))
                return w.AsWindow();
            if (w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyButton")) != null)
                return w.AsWindow();
        }
        catch { }
    }
    return null;
}

static void DumpWindows(UIA3Automation automation)
{
    try
    {
        var desktop = automation.GetDesktop();
        foreach (var w in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            Log($"  window name='{w.Properties.Name.ValueOrDefault}' class='{w.Properties.ClassName.ValueOrDefault}' id='{w.Properties.AutomationId.ValueOrDefault}'");
    }
    catch (Exception ex) { Log($"dump windows failed: {ex.Message}"); }
}

static string GetComboText(AutomationElement el)
{
    var cb = el.AsComboBox();
    // Expand so the ComboBoxItem containers are realized, then read the selected
    // item's rendered text (the real visual). WPF's automation Value returns
    // ToString() for a programmatically-set selection until the items are realized.
    bool expanded = false;
    try { cb.Expand(); expanded = true; } catch { }
    try
    {
        var sel = cb.SelectedItem;
        if (sel != null)
        {
            if (!string.IsNullOrEmpty(sel.Text)) return sel.Text;
            if (!string.IsNullOrEmpty(sel.Name)) return sel.Name;
        }
    }
    catch { }
    finally
    {
        if (expanded) { try { cb.Collapse(); } catch { } }
    }

    try
    {
        if (el.Patterns.Value.IsSupported)
            return el.Patterns.Value.Pattern.Value ?? "";
    }
    catch { }
    return "";
}

static void SetComboText(Window settings, string id, string text)
{
    try
    {
        var el = settings.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (el == null) { Log($"SetComboText: {id} not found"); return; }
        el.AsComboBox().EditableText = text;
        Log($"set {id} = '{text}'");
    }
    catch (Exception ex) { Log($"SetComboText {id} failed: {ex.Message}"); }
}

static void SaveShot(AutomationElement? el, string path)
{
    if (el == null) { Log($"shot skipped (null) {path}"); return; }
    try
    {
        using var img = el.Capture();
        img.Save(path, ImageFormat.Png);
        Log($"shot saved {path}");
    }
    catch (Exception ex) { Log($"shot failed {path}: {ex.Message}"); }
}

static void Log(string s) => Console.WriteLine($"[UiTest] {s}");
