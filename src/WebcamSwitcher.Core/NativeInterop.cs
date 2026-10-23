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
    public const uint FileFlagOverlapped = 0x40000000;

    public const int ErrorIoPending = 997;
    public const int ErrorPipeConnected = 535;
    public const int ErrorBrokenPipe = 109;
    public const int ErrorNoData = 232;
    public const int ErrorTimeout = 121;

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint OpenExisting = 0x3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBuf, uint inBuf, uint defaultTimeout, IntPtr secAttr);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, IntPtr buffer, uint count, IntPtr bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

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
