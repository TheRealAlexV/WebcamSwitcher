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

    /// <summary>Media source DLL shipped next to the app (the registered path).</summary>
    private static readonly string SourceDll =
        Path.Combine(AppContext.BaseDirectory, "WebcamSwitcher.Source.dll");

    /// <summary>HKLM COM registration key for the media source (stores an absolute DLL path).</summary>
    private const string SourceClsidKey =
        @"Software\Classes\CLSID\" + Protocol.SourceClsid + @"\InprocServer32";

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

        // Fail loudly and actionably - never crash - if the native media source or
        // its COM registration is unavailable (e.g. the app was moved after
        // regsvr32, or a build was interrupted before the DLL was copied next to
        // the app). Otherwise the DllImport below throws DllNotFoundException,
        // which escapes as an unhandled exception and terminates the process.
        if (!PreflightNativeSource())
            return false;

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
            int hr;
            IntPtr handle;
            try
            {
                hr = NativeInterop.WebcamSwitcher_Start(names[i], Protocol.SourceClsid, out handle);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                // Belt and braces: even if the pre-flight passed, a broken or
                // mismatched native DLL must not take the app down.
                AppLog.Write($"VirtualCameraService: native media source unavailable ({ex.GetType().Name}: {ex.Message}). " +
                             "Rebuild with build.ps1 and re-run regsvr32 if the app moved.");
                break;
            }

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

    /// <summary>
    /// The media source is registered in HKLM by absolute DLL path (see
    /// <c>WebcamSwitcher.Source/DllRegisterServer</c>). Moving the app - or
    /// deleting its build output - after registration leaves that path dangling,
    /// and <c>MFCreateVirtualCamera</c> then fails with 0x8007007E
    /// (ERROR_MOD_NOT_FOUND), so activation silently does nothing. Detect it up
    /// front and log an actionable fix instead of an opaque HRESULT.
    /// </summary>
    /// <returns>
    /// False when the virtual cameras cannot possibly start (DLL missing, or the
    /// registration is absent or points at a deleted file); true otherwise.
    /// </returns>
    private static bool PreflightNativeSource()
    {
        // The DllImport below resolves against the app directory. If the file is
        // gone, calling into it throws DllNotFoundException and kills the process,
        // so refuse to call it at all.
        if (!File.Exists(SourceDll))
        {
            AppLog.Write($"VirtualCameraService: '{SourceDll}' is missing - run build.ps1 to restore it. " +
                         "Virtual cameras are unavailable until then.");
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SourceClsidKey);

            if (key?.GetValue(null) is not string registered || registered.Length == 0)
            {
                AppLog.Write($"VirtualCameraService: media source not registered in HKLM ({SourceClsidKey}). " +
                             $"Fix (elevated): regsvr32 /s \"{SourceDll}\"");
                return false;
            }

            if (!File.Exists(registered))
            {
                AppLog.Write($"VirtualCameraService: media source registration is stale - '{registered}' does not exist. " +
                             $"Fix (elevated): regsvr32 /s \"{SourceDll}\"");
                return false;
            }
        }
        catch (Exception ex)
        {
            // An unreadable key (permissions etc.) must not block a legitimate
            // start - let the create call decide.
            AppLog.Write($"VirtualCameraService: registration check failed: {ex.Message}");
        }

        return true;
    }

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
