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
    private readonly string _pipeName;
    private readonly byte[] _configMsg;
    private readonly IntPtr _securityDescriptor;
    private readonly AutoResetEvent _frameSignal = new(false);
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private Task? _writerTask;
    private byte[] _pending = Array.Empty<byte>();
    private byte[] _writeBuf = Array.Empty<byte>();
    private long _publishCount;
    private long _startTicks;
    private int _disposed;

    public FramePublisher(string pipeName, int width, int height, int fps)
    {
        _pipeName = pipeName;
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

    public void Start()
    {
        _startTicks = DateTime.UtcNow.Ticks;
        _acceptTask = Task.Run(AcceptLoop);
        _writerTask = Task.Run(WriterLoop);
    }

    /// <summary>Queues the latest NV12 frame for delivery to every connected client.</summary>
    public void PublishFrame(byte[] nv12, WsFrameInfo info)
    {
        byte[] infoBytes = StructToBytes(info);
        byte[] header = StructToBytes(new WsMsgHeader
        {
            Magic = Protocol.Magic,
            Version = Protocol.Version,
            Type = Protocol.TypeFrame,
            PayloadLen = (uint)(infoBytes.Length + nv12.Length)
        });
        int total = header.Length + infoBytes.Length + nv12.Length;

        // Copy into a contiguous latest-frame slot; the writer thread performs the
        // (possibly blocking) pipe writes so a slow consumer can't stall capture.
        lock (_lock)
        {
            if (_pending.Length != total)
                _pending = new byte[total];
            Buffer.BlockCopy(header, 0, _pending, 0, header.Length);
            Buffer.BlockCopy(infoBytes, 0, _pending, header.Length, infoBytes.Length);
            Buffer.BlockCopy(nv12, 0, _pending, header.Length + infoBytes.Length, nv12.Length);
            _frameSignal.Set();
        }
        Interlocked.Increment(ref _publishCount);
    }

    private void WriterLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!_frameSignal.WaitOne(500))
                continue;

            byte[] msg;
            lock (_lock)
            {
                if (_writeBuf.Length != _pending.Length)
                    _writeBuf = new byte[_pending.Length];
                Buffer.BlockCopy(_pending, 0, _writeBuf, 0, _pending.Length);
                msg = _writeBuf;
            }

            IntPtr[] clients;
            lock (_lock)
                clients = _clients.ToArray();

            foreach (var h in clients)
            {
                if (!WriteAll(h, msg))
                {
                    lock (_lock)
                    {
                        if (_clients.Contains(h))
                        {
                            NativeInterop.DisconnectNamedPipe(h);
                            NativeInterop.CloseHandle(h);
                            _clients.Remove(h);
                            AppLog.Write($"FramePublisher: client write failed -> dropped (clients={_clients.Count})");
                        }
                    }
                }
            }
        }
    }

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    /// <summary>Total frames handed to this publisher. Monotonic; for diagnostics/tests.</summary>
    public long PublishCount => Interlocked.Read(ref _publishCount);

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
                _pipeName,
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
            AppLog.Write($"FramePublisher: client connected (clients={ClientCount})");
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
        _frameSignal.Set();
        try { _acceptTask?.Wait(2000); } catch { }
        try { _writerTask?.Wait(2000); } catch { }

        double secs = (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerSecond;
        if (secs > 0)
            AppLog.Write($"FramePublisher: published {_publishCount} frames over {secs:F1}s = {_publishCount / secs:F1} fps");

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
    private void UnblockAccept()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                IntPtr h = NativeInterop.CreateFileW(_pipeName, NativeInterop.GenericRead, NativeInterop.FileShareRead | NativeInterop.FileShareWrite, IntPtr.Zero, NativeInterop.OpenExisting, 0, IntPtr.Zero);
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
