using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WebcamSwitcher.Core;

public static class NativeInterop
{
    // ---- virtual camera shim (WebcamSwitcher.Source.dll) ----
    [DllImport("WebcamSwitcher.Source.dll", CharSet = CharSet.Unicode)]
    public static extern int WebcamSwitcher_Start(
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        [MarshalAs(UnmanagedType.LPWStr)] string sourceId,
        out IntPtr handle);

    [DllImport("WebcamSwitcher.Source.dll")]
    public static extern void WebcamSwitcher_Stop(IntPtr handle);

    // ---- named pipe ----
    public const uint PipeAccessDuplex = 0x3;
    public const uint PipeTypeByte = 0x0;
    public const uint PipeReadmodeByte = 0x0;
    public const uint PipeWait = 0x0;
    public const uint PipeUnlimitedInstances = 255;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBuf, uint inBuf, uint defaultTimeout, IntPtr secAttr);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, byte[] buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DisconnectNamedPipe(IntPtr pipe);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---- security descriptor ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint sddlRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    // ---- global hotkeys ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
