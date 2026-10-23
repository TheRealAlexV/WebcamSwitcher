using System.Text;

namespace WebcamSwitcher.Core;

/// <summary>
/// Native seam for creating/stopping one Windows 11 Media Foundation virtual
/// camera. Production uses <see cref="InteropVirtualCameraNative"/>; tests inject
/// a fake so the slot logic can be exercised without registering real devices.
/// </summary>
public interface IVirtualCameraNative
{
    /// <summary>Creates and starts one virtual camera. Returns an HRESULT.</summary>
    int Start(string friendlyName, string sourceId, out IntPtr handle);

    /// <summary>Stops and releases one virtual camera handle.</summary>
    void Stop(IntPtr handle);
}

/// <summary>Default <see cref="IVirtualCameraNative"/> over the shipped DLL shim.</summary>
internal sealed class InteropVirtualCameraNative : IVirtualCameraNative
{
    public int Start(string friendlyName, string sourceId, out IntPtr handle)
    {
        try
        {
            return NativeInterop.WebcamSwitcher_Start(friendlyName, sourceId, out handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            // A broken or mismatched native DLL must never take the app down.
            AppLog.Write($"VirtualCameraService: native media source unavailable ({ex.GetType().Name}: {ex.Message}). " +
                         "Rebuild with build.ps1 and re-run regsvr32 if the app moved.");
            handle = IntPtr.Zero;
            return unchecked((int)0x8007007E); // ERROR_MOD_NOT_FOUND
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: native start threw {ex.GetType().Name}: {ex.Message}");
            handle = IntPtr.Zero;
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    public void Stop(IntPtr handle)
    {
        try { NativeInterop.WebcamSwitcher_Stop(handle); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            AppLog.Write($"VirtualCameraService: native stop unavailable ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: native stop threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// Manages the Windows 11 Media Foundation virtual cameras via the native shim
/// in WebcamSwitcher.Source.dll (MFCreateVirtualCamera + Start/Stop), one slot at
/// a time.
///
/// Slots are numbered over the whole configuration: <c>0..cameras.Count-1</c> are
/// the passthrough cameras, <c>cameras.Count</c> is the switcher camera. Friendly
/// names, pipes and the vcams.ini mapping are computed for every slot regardless
/// of which are enabled, so a slot never renames itself or a neighbour because a
/// different slot is off, and a slot can be started later without a restart.
/// </summary>
public sealed class VirtualCameraService : IDisposable
{
    /// <summary>Friendly name of the switcher virtual camera (unchanged).</summary>
    public const string SwitcherName = "WebcamSwitcher Virtual Camera";

    private static readonly string DefaultVcamsIniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WebcamSwitcher",
        "vcams.ini");

    /// <summary>Media source DLL shipped next to the app (the registered path).</summary>
    private static readonly string SourceDll =
        Path.Combine(AppContext.BaseDirectory, "WebcamSwitcher.Source.dll");

    /// <summary>HKLM COM registration key for the media source (stores an absolute DLL path).</summary>
    private const string SourceClsidKey =
        @"Software\Classes\CLSID\" + Protocol.SourceClsid + @"\InprocServer32";

    private readonly IVirtualCameraNative _native;
    private readonly string _vcamsIniPath;

    // Tests inject a fake native layer, which has no DLL/COM registration to check.
    private readonly bool _skipPreflight;

    // Serialises Start/Stop per slot: one native create/stop can take ~1 s, and a
    // second call for the same slot must wait rather than race. Different slots
    // use different locks and proceed independently.
    private readonly object _slotsLock = new();
    private readonly List<object> _slotLocks = new();

    private readonly List<string> _names = new();
    private readonly List<string> _pipes = new();
    private readonly List<IntPtr> _handles = new();

    public VirtualCameraService() : this(new InteropVirtualCameraNative(), null)
    {
    }

    /// <summary>Test constructor: fake native layer and/or a scratch vcams.ini.</summary>
    internal VirtualCameraService(IVirtualCameraNative native, string? vcamsIniPath, bool skipPreflight = false)
    {
        _native = native;
        _vcamsIniPath = vcamsIniPath ?? DefaultVcamsIniPath;
        _skipPreflight = skipPreflight;
    }

    /// <summary>Number of slots: one per camera plus the switcher.</summary>
    public int SlotCount
    {
        get { lock (_slotsLock) return _names.Count; }
    }

    /// <summary>True while at least one virtual camera handle is held.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_slotsLock)
            {
                foreach (var h in _handles)
                    if (h != IntPtr.Zero)
                        return true;
                return false;
            }
        }
    }

    public string FriendlyName => SwitcherName;

    /// <summary>Friendly name of a slot, or null when the slot does not exist.</summary>
    public string? SlotName(int index)
    {
        lock (_slotsLock)
            return index >= 0 && index < _names.Count ? _names[index] : null;
    }

    /// <summary>True when that slot currently holds a live native handle.</summary>
    public bool IsSlotRunning(int index)
    {
        lock (_slotsLock)
            return index >= 0 && index < _handles.Count && _handles[index] != IntPtr.Zero;
    }

    /// <summary>Test hook: the native handle held by a slot (IntPtr.Zero when stopped).</summary>
    internal IntPtr SlotHandleForTest(int index)
    {
        lock (_slotsLock)
            return index >= 0 && index < _handles.Count ? _handles[index] : IntPtr.Zero;
    }

    /// <summary>
    /// Computes the deduped friendly names and pipes for every slot and writes
    /// the routing file, without creating anything. Idempotent: the same camera
    /// list always yields the same names. If the list changed while slots were
    /// running, those slots are stopped first (they would no longer match).
    /// </summary>
    public bool Prepare(IReadOnlyList<CameraConfig>? cameras)
    {
        List<string> names;
        List<string> pipes;
        lock (_slotsLock)
        {
            names = BuildNames(cameras, out pipes);
            if (SequenceEqual(names, _names))
                return true;
        }

        // Mapping changed: existing handles refer to the old names.
        if (IsRunning)
        {
            AppLog.Write("VirtualCameraService: camera list changed while virtual cameras were running -> stopping all slots");
            Stop();
        }

        lock (_slotsLock)
        {
            _names.Clear();
            _names.AddRange(names);
            _pipes.Clear();
            _pipes.AddRange(pipes);
            _handles.Clear();
            for (int i = 0; i < names.Count; i++)
                _handles.Add(IntPtr.Zero);
            while (_slotLocks.Count < names.Count)
                _slotLocks.Add(new object());
        }

        WriteVcamsIni(names, pipes);
        return true;
    }

    /// <summary>
    /// Starts every slot for which <paramref name="enabled"/> returns true.
    /// Returns whether any slot ended up running.
    /// </summary>
    public bool StartEnabled(IReadOnlyList<CameraConfig>? cameras, Func<int, bool> enabled)
    {
        if (!_skipPreflight && !PreflightNativeSource())
            return false;

        Prepare(cameras);

        int count = SlotCount;
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            if (enabled != null && !enabled(i))
                continue;
            if (StartSlot(i))
                any = true;
        }
        return any;
    }

    /// <summary>
    /// Registers and starts one passthrough virtual camera per camera plus the
    /// switcher virtual camera. Kept for callers that want every slot.
    /// Returns whether the switcher virtual camera started.
    /// </summary>
    public bool Start(IReadOnlyList<CameraConfig> cameras) => StartEnabled(cameras, _ => true);

    /// <summary>
    /// Creates and starts exactly one slot. Safe to call repeatedly: a running
    /// slot is a no-op. Never throws.
    /// </summary>
    public bool StartSlot(int index)
    {
        object gate;
        string name;
        lock (_slotsLock)
        {
            if (index < 0 || index >= _names.Count)
            {
                AppLog.Write($"VirtualCameraService.StartSlot({index}): no such slot");
                return false;
            }
            gate = _slotLocks[index];
            name = _names[index];
        }

        lock (gate)
        {
            lock (_slotsLock)
            {
                if (_handles[index] != IntPtr.Zero)
                    return true; // already running
            }

            if (!_skipPreflight && !PreflightNativeSource())
                return false;

            int hr = _native.Start(name, Protocol.SourceClsid, out IntPtr handle);
            if (hr < 0 || handle == IntPtr.Zero)
            {
                AppLog.Write($"VirtualCameraService.Start('{name}') slot {index} failed hr=0x{hr:X8}" +
                             (handle == IntPtr.Zero && hr >= 0 ? " (null handle)" : string.Empty));
                return false;
            }

            lock (_slotsLock)
                _handles[index] = handle;

            AppLog.Write($"VirtualCameraService.Start('{name}') slot {index} ok");
            return true;
        }
    }

    /// <summary>
    /// Stops exactly one slot: shuts its virtual camera down so the device is no
    /// longer enumerable and releases the handle. Other slots are untouched.
    /// Safe to call repeatedly and on a slot that is not running. Never throws.
    /// </summary>
    public bool StopSlot(int index)
    {
        object gate;
        string? name;
        lock (_slotsLock)
        {
            if (index < 0 || index >= _names.Count)
            {
                AppLog.Write($"VirtualCameraService.StopSlot({index}): no such slot");
                return false;
            }
            gate = _slotLocks[index];
            name = _names[index];
        }

        lock (gate)
        {
            IntPtr handle;
            lock (_slotsLock)
            {
                handle = _handles[index];
                if (handle == IntPtr.Zero)
                    return true; // already stopped
                _handles[index] = IntPtr.Zero; // never hand this handle out twice
            }

            try
            {
                _native.Stop(handle);
                AppLog.Write($"VirtualCameraService.Stop('{name}') slot {index} ok");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"VirtualCameraService.Stop('{name}') slot {index} threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Stops every started virtual camera. Idempotent.</summary>
    public void Stop()
    {
        int count = SlotCount;
        for (int i = 0; i < count; i++)
            StopSlot(i);
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

    /// <summary>
    /// Names for every slot, in slot order: each camera, then the switcher.
    /// Dedupe is applied over the whole list, so the result depends only on the
    /// camera list - never on which slots are enabled.
    /// </summary>
    private static List<string> BuildNames(IReadOnlyList<CameraConfig>? cameras, out List<string> pipes)
    {
        int count = cameras?.Count ?? 0;
        var names = new List<string>(count + 1);
        pipes = new List<string>(count + 1);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            string baseName = $"{(cameras![i].FriendlyName ?? string.Empty)} (WebcamSwitcher)";
            names.Add(Dedupe(baseName, used));
            pipes.Add(Protocol.PassthroughPipeName(i));
        }

        names.Add(Dedupe(SwitcherName, used));
        pipes.Add(Protocol.PipeName);
        return names;
    }

    private static bool SequenceEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
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

    private void WriteVcamsIni(IReadOnlyList<string> names, IReadOnlyList<string> pipes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_vcamsIniPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 without BOM, CRLF line endings.
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
                sb.Append(names[i]).Append('\t').Append(pipes[i]).Append("\r\n");

            File.WriteAllText(_vcamsIniPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AppLog.Write($"VirtualCameraService: wrote {names.Count} mapping(s) to {_vcamsIniPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"VirtualCameraService: failed to write {_vcamsIniPath}: {ex.Message}");
        }
    }
}
