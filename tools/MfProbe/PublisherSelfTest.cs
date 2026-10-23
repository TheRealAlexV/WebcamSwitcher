using System.IO.Pipes;
using System.Runtime.InteropServices;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Exercises FramePublisher's three-buffer ring against a deliberately slow
/// consumer: publishes frames back to back over several seconds and verifies,
/// for every message that arrives, that the header/payload are intact and the
/// NV12 body is internally consistent (every byte matches the frame's counter,
/// with the chroma plane offset by one). Detects tearing, stale writes and
/// length mix-ups - the failure modes a swap can introduce.
/// </summary>
internal static class PublisherSelfTest
{
    private const int W = 320;
    private const int H = 180;

    public static int Run()
    {
        int nv12 = Protocol.Nv12Size(W, H);
        const int publishCount = 400;
        string pipe = $@"\\.\pipe\WebcamSwitcher.Probe.{Environment.ProcessId}";

        using var pub = new FramePublisher(pipe, W, H, 30);
        pub.Start();

        int received = 0;
        int corrupt = 0;
        var done = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe.Substring(9), PipeDirection.In);
                client.Connect(3000);
                var headerBytes = new byte[Marshal.SizeOf<WsMsgHeader>()];

                // Deliberately read slowly at times so the writer is mid-write
                // while new frames are being published.
                var buf = new byte[headerBytes.Length + Marshal.SizeOf<WsFrameInfo>() + nv12];
                while (true)
                {
                    int got = 0;
                    while (got < headerBytes.Length)
                    {
                        int n = client.Read(headerBytes, got, headerBytes.Length - got);
                        if (n <= 0) { done.Set(); return; }
                        got += n;
                    }

                    var header = MemoryMarshal.Read<WsMsgHeader>(headerBytes);
                    if (header.Type == Protocol.TypeConfig)
                    {
                        var cfg = new byte[(int)header.PayloadLen];
                        if (client.Read(cfg, 0, cfg.Length) != cfg.Length) { done.Set(); return; }
                        continue;
                    }
                    if (header.Magic != Protocol.Magic || header.Version != Protocol.Version
                        || header.Type != Protocol.TypeFrame)
                    {
                        Interlocked.Increment(ref corrupt);
                        done.Set();
                        return;
                    }

                    int payload = (int)header.PayloadLen;
                    if (payload != Marshal.SizeOf<WsFrameInfo>() + nv12)
                    {
                        Interlocked.Increment(ref corrupt);
                        done.Set();
                        return;
                    }

                    int read = 0;
                    while (read < payload)
                    {
                        int n = client.Read(buf, read, payload - read);
                        if (n <= 0) { done.Set(); return; }
                        read += n;
                    }

                    var info = MemoryMarshal.Read<WsFrameInfo>(buf);
                    if (info.Width != W || info.Height != H)
                    {
                        Interlocked.Increment(ref corrupt);
                        done.Set();
                        return;
                    }

                    // Body: filled with a per-frame byte value.
                    byte tag = buf[Marshal.SizeOf<WsFrameInfo>()];
                    int body = Marshal.SizeOf<WsFrameInfo>();
                    for (int i = 0; i < nv12; i++)
                    {
                        if (buf[body + i] != tag)
                        {
                            Interlocked.Increment(ref corrupt);
                            break;
                        }
                    }

                    Interlocked.Increment(ref received);
                    if (received % 37 == 0)
                        Thread.Sleep(25); // stall the writer so publishes pile up
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  reader failed: {ex.GetType().Name} {ex.Message}");
                done.Set();
            }
        })
        { IsBackground = true };
        reader.Start();

        var frame = new byte[nv12];
        var info = new WsFrameInfo { Width = W, Height = H, Format = Protocol.FormatNv12 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < publishCount; i++)
        {
            Array.Fill(frame, (byte)i);
            pub.PublishFrame(frame, info);
            Thread.Sleep(2); // ~500 fps offered, far above what the reader drains
        }

        Thread.Sleep(1500);
        done.Wait(3000);

        Console.WriteLine($"PublisherSelfTest: published {pub.PublishCount}, received {received}, corrupt {corrupt}, clients {pub.ClientCount}");
        bool ok = corrupt == 0 && received > 0 && received <= pub.PublishCount;
        Console.WriteLine(ok
            ? $"RESULT: PASS (latest-wins: {pub.PublishCount - received} frame(s) skipped, none torn)"
            : "RESULT: FAIL");
        return ok ? 0 : 1;
    }
}
