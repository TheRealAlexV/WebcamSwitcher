using System.Runtime.InteropServices;

namespace WebcamSwitcher.Core;

/// <summary>
/// Serves frames to the virtual-camera media source over the named pipe.
/// The app is the pipe server; the C++ source (loaded by the Frame Server in
/// session 0) connects as a client. The pipe DACL grants Everyone so that
/// SYSTEM / LOCAL SERVICE can connect cross-session.
/// </summary>
public sealed class FramePublisher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    private readonly object _lock = new();
    private readonly List<IntPtr> _clients = new();
    private readonly byte[] _configMsg;
    private readonly IntPtr _securityDescriptor;
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private int _disposed;

    public FramePublisher(int width, int height, int fps)
    {
        var config = new WsConfigMsg
        {
            Width = (uint)width,
            Height = (uint)height,
            Stride = (uint)width,
            Format = Protocol.FormatNv12,
            FpsNum = (uint)fps,
            FpsDen = 1
        };
        _configMsg = BuildMessage(Protocol.TypeConfig, StructToBytes(config));

        if (!NativeInterop.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;WD)", 1, out _securityDescriptor, out _))
            _securityDescriptor = IntPtr.Zero;
    }

    public void Start() => _acceptTask = Task.Run(AcceptLoop);

    /// <summary>Writes the latest NV12 frame to every connected client.</summary>
    public void PublishFrame(byte[] nv12, WsFrameInfo info)
    {
        byte[] header = BuildMessage(Protocol.TypeFrame, StructToBytes(info));
        lock (_lock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var h = _clients[i];
                if (!WriteAll(h, header) || !WriteAll(h, nv12))
                {
                    NativeInterop.DisconnectNamedPipe(h);
                    NativeInterop.CloseHandle(h);
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    private unsafe void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = sizeof(SECURITY_ATTRIBUTES),
                lpSecurityDescriptor = _securityDescriptor,
                bInheritHandle = 0
            };

            IntPtr pipe = NativeInterop.CreateNamedPipeW(
                Protocol.PipeName,
                NativeInterop.PipeAccessDuplex,
                NativeInterop.PipeTypeByte | NativeInterop.PipeReadmodeByte | NativeInterop.PipeWait,
                NativeInterop.PipeUnlimitedInstances,
                1 * 1024 * 1024, 0, 0, (IntPtr)(&sa));

            if (pipe == (IntPtr)(-1))
            {
                Thread.Sleep(200);
                continue;
            }

            NativeInterop.ConnectNamedPipe(pipe, IntPtr.Zero);
            if (_cts.IsCancellationRequested)
            {
                NativeInterop.CloseHandle(pipe);
                break;
            }

            // Send config on connect.
            if (!WriteAll(pipe, _configMsg))
            {
                NativeInterop.DisconnectNamedPipe(pipe);
                NativeInterop.CloseHandle(pipe);
                continue;
            }

            lock (_lock)
                _clients.Add(pipe);
        }
    }

    private static bool WriteAll(IntPtr pipe, byte[] data)
    {
        uint total = 0;
        while (total < data.Length)
        {
            if (!NativeInterop.WriteFile(pipe, data, (uint)(data.Length - total), out uint written, IntPtr.Zero) || written == 0)
                return false;
            total += written;
        }
        return true;
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] buf = new byte[size];
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, p, false);
            Marshal.Copy(p, buf, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
        return buf;
    }

    private static byte[] BuildMessage(ushort type, byte[] payload)
    {
        var header = new WsMsgHeader { Magic = Protocol.Magic, Version = Protocol.Version, Type = type, PayloadLen = (uint)payload.Length };
        var hb = StructToBytes(header);
        byte[] msg = new byte[hb.Length + payload.Length];
        Buffer.BlockCopy(hb, 0, msg, 0, hb.Length);
        Buffer.BlockCopy(payload, 0, msg, hb.Length, payload.Length);
        return msg;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        UnblockAccept();
        try { _acceptTask?.Wait(2000); } catch { }
        lock (_lock)
        {
            foreach (var h in _clients)
            {
                NativeInterop.DisconnectNamedPipe(h);
                NativeInterop.CloseHandle(h);
            }
            _clients.Clear();
        }
        if (_securityDescriptor != IntPtr.Zero)
            NativeInterop.LocalFree(_securityDescriptor);
        _cts.Dispose();
    }

    // Connects a throwaway client so a pending ConnectNamedPipe in AcceptLoop returns
    // and the loop can observe cancellation and exit (otherwise it would block forever
    // and leak a pipe instance + thread across rebuilds).
    private static void UnblockAccept()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                IntPtr h = NativeInterop.CreateFileW(Protocol.PipeName, NativeInterop.GenericRead, NativeInterop.FileShareRead | NativeInterop.FileShareWrite, IntPtr.Zero, NativeInterop.OpenExisting, 0, IntPtr.Zero);
                if (h != (IntPtr)(-1))
                {
                    NativeInterop.CloseHandle(h);
                    return;
                }
            }
            catch { }
            Thread.Sleep(20);
        }
    }
}
