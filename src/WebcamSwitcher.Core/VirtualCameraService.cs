namespace WebcamSwitcher.Core;

/// <summary>
/// Manages the Windows 11 Media Foundation virtual camera via the native shim
/// in WebcamSwitcher.Source.dll (MFCreateVirtualCamera + Start/Stop).
/// </summary>
public sealed class VirtualCameraService : IDisposable
{
    private IntPtr _handle;
    private bool _running;

    public bool IsRunning => _running;

    public string FriendlyName { get; }

    public VirtualCameraService(string friendlyName = "WebcamSwitcher Virtual Camera")
    {
        FriendlyName = friendlyName;
    }

    /// <summary>Registers and starts the virtual camera (Session lifetime).</summary>
    public bool Start()
    {
        if (_running)
            return true;

        int hr = NativeInterop.WebcamSwitcher_Start(FriendlyName, Protocol.SourceClsid, out _handle);
        if (hr < 0 || _handle == IntPtr.Zero)
            return false;

        _running = true;
        return true;
    }

    public void Stop()
    {
        if (!_running)
            return;

        NativeInterop.WebcamSwitcher_Stop(_handle);
        _handle = IntPtr.Zero;
        _running = false;
    }

    public void Dispose() => Stop();
}
