using System.Text;

namespace WebcamSwitcher.Core;

/// <summary>
/// Manages the Windows 11 Media Foundation virtual cameras via the native shim
/// in WebcamSwitcher.Source.dll (MFCreateVirtualCamera + Start/Stop).
///
/// On <see cref="Start"/> it registers one passthrough virtual camera per
/// configured physical camera plus the switcher virtual camera. Before creating
/// any of them it writes the friendly-name -> pipe mapping that the native media
/// source reads at activation time (<c>%ProgramData%\WebcamSwitcher\vcams.ini</c>).
/// </summary>
public sealed class VirtualCameraService : IDisposable
{
    /// <summary>Friendly name of the switcher virtual camera (unchanged).</summary>
    public const string SwitcherName = "WebcamSwitcher Virtual Camera";

    private static readonly string VcamsIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WebcamSwitcher",
        "vcams.ini");

    private readonly List<IntPtr> _handles = new();
    private bool _switcherStarted;

    /// <summary>True while at least one virtual camera handle is held.</summary>
    public bool IsRunning => _handles.Count > 0;

    public string FriendlyName => SwitcherName;

    /// <summary>
    /// Registers and starts one passthrough virtual camera per camera in
    /// <paramref name="cameras"/> (slot <c>i</c> -&gt; <c>Protocol.PassthroughPipeName(i)</c>)
    /// plus the switcher virtual camera (<see cref="Protocol.PipeName"/>).
    /// Per-camera failures are logged and skipped; returns whether the switcher
    /// virtual camera started.
    /// </summary>
    public bool Start(IReadOnlyList<CameraConfig> cameras)
    {
        if (_switcherStarted)
            return true;
        // A previous attempt may have left some passthrough handles behind; clear
        // them so a retry never leaks duplicate virtual cameras.
        if (_handles.Count > 0)
            Stop();

        int count = cameras?.Count ?? 0;

        // 1. Compute deduped friendly names. Passthrough slots come first (in slot
        //    order), then the switcher, so dedupe is deterministic.
        var names = new List<string>(count + 1);
        var pipes = new List<string>(count + 1);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            string baseName = $"{(cameras![i].FriendlyName ?? string.Empty)} (WebcamSwitcher)";
            names.Add(Dedupe(baseName, used));
            pipes.Add(Protocol.PassthroughPipeName(i));
        }

        int switcherIndex = names.Count;
        names.Add(Dedupe(SwitcherName, used));
        pipes.Add(Protocol.PipeName);

        // 2. Write the routing file FIRST, before creating any virtual camera.
        WriteVcamsIni(names, pipes);

        // 3. Register each virtual camera, keeping every handle.
        bool switcherOk = false;
        for (int i = 0; i < names.Count; i++)
        {
            int hr = NativeInterop.WebcamSwitcher_Start(names[i], Protocol.SourceClsid, out IntPtr handle);
            if (hr < 0 || handle == IntPtr.Zero)
            {
                AppLog.Write($"VirtualCameraService.Start('{names[i]}') failed hr=0x{hr:X8}");
                continue;
            }

            _handles.Add(handle);
            if (i == switcherIndex)
                switcherOk = true;
            AppLog.Write($"VirtualCameraService.Start('{names[i]}') ok");
        }

        _switcherStarted = switcherOk;
        if (!switcherOk)
            AppLog.Write("VirtualCameraService.Start: switcher virtual camera FAILED");
        return switcherOk;
    }

    /// <summary>Stops every started virtual camera. Idempotent.</summary>
    public void Stop()
    {
        foreach (var handle in _handles)
        {
            try { NativeInterop.WebcamSwitcher_Stop(handle); }
            catch { }
        }
        _handles.Clear();
        _switcherStarted = false;
    }

    public void Dispose() => Stop();

    private static string Dedupe(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName))
            return baseName;

        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} ({n})";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static void WriteVcamsIni(IReadOnlyList<string> names, IReadOnlyList<string> pipes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(VcamsIniPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 without BOM, CRLF line endings.
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
                sb.Append(names[i]).Append('\t').Append(pipes[i]).Append("\r\n");

            File.WriteAllText(VcamsIniPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AppLog.Write($"VirtualCameraService: wrote {names.Count} mapping(s) to {VcamsIniPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: failed to write {VcamsIniPath}: {ex.Message}");
        }
    }
}
