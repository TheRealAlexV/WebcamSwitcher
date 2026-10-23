using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Abuse tests for FramePublisher. Everything runs in-process against the real
/// pipe server - no cameras, no virtual-camera DLL, no Frame Server.
///
///   a) 3 publishers, 200 cycles each: client reads 5 frames and vanishes.
///   b) 30 fps publish while clients connect/disconnect at random: no torn
///      frames, no publish stall over 100 ms.
///   c) the server instance is destroyed and recreated mid-stream (app restart)
///      and the next client must recover.
/// </summary>
internal static class PublisherAbuse
{
    private const int W = 1280;
    private const int H = 720;
    private static readonly int Nv12Size = Protocol.Nv12Size(W, H);
    private static readonly int FrameInfoSize = Marshal.SizeOf<WsFrameInfo>();

    // FramePublisher needs the full \\.\pipe\ path; NamedPipeClientStream wants
    // the bare name.
    private static string Full(string tag) => Protocol.PipeName + ".abuse." + tag;
    private static string PipeName(int slot) => Full("a" + slot);
    private static string Bare(string full) =>
        full.StartsWith(@"\\.\pipe\", StringComparison.Ordinal) ? full[9..] : full;

    private static byte[] BuildFrame(ulong seq, out WsFrameInfo info)
    {
        byte fill = (byte)(0x40 + (seq * 7) % 0x80); // body is one repeated byte: torn writes show up at once
        var nv12 = new byte[Nv12Size];
        Array.Fill(nv12, fill);
        info = new WsFrameInfo
        {
            Sequence = seq,
            TimestampQpc = (ulong)Stopwatch.GetTimestamp(),
            Width = W,
            Height = H,
            Stride = W,
            Format = Protocol.FormatNv12,
            PayloadBytes = (uint)Nv12Size,
            Flags = 0
        };
        return nv12;
    }

    public static int Run()
    {
        int failures = 0;
        if (DebugOnly == 1)
            return DebugOne();
        if (DebugOnly == 2)
            return DebugRecreate();
        failures += CaseA();
        failures += CaseA2();
        failures += CaseB();
        failures += CaseC();
        Console.WriteLine(failures == 0 ? "\nRESULT: PASS" : $"\nRESULT: FAIL ({failures} failure(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>One publisher, one client, every step printed: isolates a stall.</summary>
    private static int DebugOne()
    {
        var pipe = Full("dbg");
        using var pub = new FramePublisher(pipe, W, H, 30);
        pub.Start();
        var feeder = new Thread(() =>
        {
            ulong seq = 0;
            var sw = new Stopwatch();
            while (!Volatile.Read(ref _feederStop))
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                pub.PublishFrame(body, info);
                Thread.Sleep(33);
            }
        }) { IsBackground = true };
        feeder.Start();
        Thread.Sleep(300);

        using var c = new NamedPipeClientStream(".", Bare(pipe), PipeDirection.In, PipeOptions.Asynchronous);
        var swCon = Stopwatch.StartNew();
        c.Connect(3000);
        Console.WriteLine($"connected in {swCon.Elapsed.TotalMilliseconds:F1} ms");
        using var reader = new BinaryReader(c);
        var sw = Stopwatch.StartNew();
        bool ok = ReadConfig(reader, out _);
        Console.WriteLine($"config ok={ok} after {sw.Elapsed.TotalMilliseconds:F1} ms");
        for (int i = 0; i < 5; i++)
        {
            var t = Task.Run(() => TryReadFrame(reader, out _, out var body) ? body.Length : -1);
            if (!t.Wait(5000)) { Console.WriteLine($"frame {i}: TIMEOUT after 5 s"); break; }
            Console.WriteLine($"frame {i}: bytes={t.Result} at {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        Volatile.Write(ref _feederStop, true);
        feeder.Join();
        return 0;
    }

    /// <summary>Destroy/recreate on one pipe name, every step timed and printed.</summary>
    private static int DebugRecreate()
    {
        var pipe = Full("dbg2");
        var sw = Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine($"[{sw.Elapsed.TotalMilliseconds,7:F1} ms] {what}");

        var pub = new FramePublisher(pipe, W, H, 30);
        pub.Start();
        Mark("pub1 started");
        var feeder = new Thread(() =>
        {
            ulong seq = 0;
            while (!Volatile.Read(ref _feederStop))
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                pub.PublishFrame(body, info);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        feeder.Start();

        Mark($"probe1 ok={TryGetOneFrame(pipe, 3000, out double m1)} in {m1:F1} ms");
        Volatile.Write(ref _feederStop, true);
        feeder.Join();
        pub.Dispose();
        Mark("pub1 disposed");
        Thread.Sleep(300);

        bool down = TryGetOneFrame(pipe, 400, out _, out string? e0);
        Mark($"probe(down) ok={down} error={e0}");

        var pub2 = new FramePublisher(pipe, W, H, 30);
        pub2.Start();
        Mark("pub2 started");
        var feeder2 = new Thread(() =>
        {
            ulong seq = 0;
            while (!Volatile.Read(ref _feederStop))
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                pub2.PublishFrame(body, info);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _feederStop = false;
        feeder2.Start();

        for (int i = 0; i < 3; i++)
        {
            bool ok = TryGetOneFrame(pipe, 3000, out double m, out string? e);
            Mark($"probe2[{i}] ok={ok} in {m:F1} ms error={e}");
            Thread.Sleep(200);
        }

        Volatile.Write(ref _feederStop, true);
        feeder2.Join();
        pub2.Dispose();
        Mark("pub2 disposed");
        return 0;
    }

    // ---- a) 200 abrupt client deaths per publisher -------------------------
    private static int CaseA()
    {
        Console.WriteLine("=== A) abrupt client death, 200 cycles x 3 publishers");
        const int publishers = 3;
        const int cycles = 200;

        var pubs = new FramePublisher[publishers];
        var feederThreads = new Thread[publishers];
        var latencies = new double[publishers];
        var worst = new double[publishers];
        var received = new int[publishers];
        var stopped = new ManualResetEventSlim();
        var failures = new int[publishers];

        for (int p = 0; p < publishers; p++)
        {
            int slot = p;
            pubs[p] = new FramePublisher(PipeName(slot), W, H, 30);
            pubs[p].Start();
            feederThreads[p] = new Thread(() =>
            {
                ulong seq = 0;
                var body = BuildFrame(0, out var info);
                var sw = new Stopwatch();
                while (!stopped.IsSet)
                {
                    seq++;
                    body = BuildFrame(seq, out info);
                    pubs[slot].PublishFrame(body, info);
                    Thread.Sleep(2); // ~500 fps: first frame must arrive in a few ms
                }
            }) { IsBackground = true };
            feederThreads[p].Start();
        }
        Thread.Sleep(300); // let the accept loops listen

        var clientThreads = new Thread[publishers];
        for (int p = 0; p < publishers; p++)
        {
            int slot = p;
            clientThreads[p] = new Thread(() =>
            {
                var sw = new Stopwatch();
                for (int i = 0; i < cycles; i++)
                {
                    try
                    {
                        using var c = new NamedPipeClientStream(".", Bare(PipeName(slot)), PipeDirection.In, PipeOptions.Asynchronous);
                        sw.Restart();
                        c.Connect(2000);
                        using var reader = new BinaryReader(c);
                        // Config first, per protocol.
                        if (!ReadConfig(reader, out _))
                        {
                            Interlocked.Increment(ref failures[slot]);
                            continue;
                        }
                        for (int f = 0; f < 5; f++)
                        {
                            if (!TryReadFrame(reader, out _, out _))
                            {
                                Interlocked.Increment(ref failures[slot]);
                                break;
                            }
                            if (f == 0)
                            {
                                double ms = sw.Elapsed.TotalMilliseconds;
                                lock (latencies)
                                {
                                    latencies[slot] += ms;
                                    if (ms > worst[slot]) worst[slot] = ms;
                                }
                                Interlocked.Increment(ref received[slot]);
                            }
                        }
                        // Abrupt: no Shutdown, just drop the handle mid-stream.
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  a{slot} cycle {i}: {ex.GetType().Name} {ex.Message}");
                        Interlocked.Increment(ref failures[slot]);
                    }
                }
            });
            clientThreads[p].Start();
        }

        foreach (var t in clientThreads) t.Join();
        stopped.Set();
        foreach (var t in feederThreads) t.Join();
        Thread.Sleep(200);

        // The publishers must still be publishing and accepting.
        for (int p = 0; p < publishers; p++)
        {
            bool alive = TryGetOneFrame(PipeName(p), 2000, out double ms, out _);
            Console.WriteLine($"  a{p}: cycles={cycles} connect+first-frame avg={latencies[p] / Math.Max(1, received[p]):F1} ms  worst={worst[p]:F1} ms  " +
                              $"frames received={received[p]}  after-all-clients first frame in {ms:F1} ms  alive={alive}");
            if (!alive) Interlocked.Increment(ref failures[p]);

            // Still a well-formed, responsive publisher after 200 client deaths.
            bool writerAlive = pubs[p].ClientCount >= 0;
            pubs[p].Dispose();
            if (!writerAlive) Interlocked.Increment(ref failures[p]);
        }

        for (int p = 0; p < publishers; p++)
        {
            if (received[p] < cycles) Interlocked.Increment(ref failures[p]);
            if (worst[p] > 200) Interlocked.Increment(ref failures[p]);
        }

        Console.WriteLine($"  worst-case first-frame latency across all cycles: {worst.Max():F1} ms (limit 200)  client cycles served: {received.Sum()}/{cycles * publishers}");
        return failures.Sum();
    }

    // ---- a2) connect while publishing is paused ----------------------------
    private static int CaseA2()
    {
        int failures = 0;
        // ---- A2) a client that connects while nothing is being published -----
        // The newest frame must be retained and resent, or a consumer attaches to a
        // live vcam and shows nothing until the next capture frame (or forever, if
        // capture stopped).
        Console.WriteLine("\n=== A2) connect while publishing is paused (retained frame)");
        {
            var p2 = Full("a2");
            using var pub2 = new FramePublisher(p2, W, H, 30);
            pub2.Start();
            var body = BuildFrame(1, out var info);
            pub2.PublishFrame(body, info);
            Thread.Sleep(200); // writer has written it, no client was attached

            bool ok = TryGetOneFrame(p2, 1000, out double ms, out string? err);
            Console.WriteLine($"  paused publisher: first frame in {ms:F1} ms  ok={ok}  error={err ?? "none"}");
            if (!ok || ms > 200) failures++;
        }

        return failures;
    }

    // ---- b) 30 fps publish, random connect/disconnect ----------------------
    private static int CaseB()
    {
        Console.WriteLine("\n=== B) 30 fps publish with random connect/disconnect");
        var pipe = Full("b");
        using var pub = new FramePublisher(pipe, W, H, 30);
        pub.Start();

        var stop = new ManualResetEventSlim();
        double maxPublishMs = 0;
        long published = 0;

        var publisher = new Thread(() =>
        {
            ulong seq = 0;
            var sw = new Stopwatch();
            while (!stop.IsSet)
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                sw.Restart();
                pub.PublishFrame(body, info);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > maxPublishMs) maxPublishMs = ms;
                published++;
                Thread.Sleep(33); // 30 fps
            }
        }) { IsBackground = true };
        publisher.Start();

        var rnd = new Random(1234);
        long framesRead = 0, torn = 0, connects = 0;
        var end = DateTime.UtcNow.AddSeconds(6);
        var readers = new List<Thread>();
        var errors = new object();

        while (DateTime.UtcNow < end)
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var c = new NamedPipeClientStream(".", Bare(pipe), PipeDirection.In, PipeOptions.Asynchronous);
                    c.Connect(2000);
                    Interlocked.Increment(ref connects);
                    using var reader = new BinaryReader(c);
                    ReadConfig(reader, out _);
                    var localEnd = DateTime.UtcNow.AddMilliseconds(rnd.Next(20, 120));
                    while (DateTime.UtcNow < localEnd)
                    {
                        if (!TryReadFrame(reader, out var info, out var body))
                            return;
                        Interlocked.Increment(ref framesRead);
                        byte expected = (byte)(0x40 + (info.Sequence * 7) % 0x80);
                        foreach (byte b in body)
                        {
                            if (b != expected)
                            {
                                Interlocked.Increment(ref torn);
                                break;
                            }
                        }
                        if (info.PayloadBytes != Nv12Size || info.Width != W || info.Height != H || body.Length != Nv12Size)
                            Interlocked.Increment(ref torn);
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) Console.WriteLine($"  b: reader error {ex.GetType().Name}: {ex.Message}");
                }
            }) { IsBackground = true };
            t.Start();
            readers.Add(t);
            Thread.Sleep(rnd.Next(5, 40));
        }

        stop.Set();
        publisher.Join();
        foreach (var t in readers) t.Join(3000);

        Console.WriteLine($"  published={published}  client sessions={connects}  frames read={framesRead}  torn={torn}  " +
                          $"max PublishFrame stall={maxPublishMs:F2} ms (limit 100)");
        bool ok = torn == 0 && maxPublishMs < 100 && published > 100 && framesRead > 0;
        Console.WriteLine($"  B: {(ok ? "OK" : "FAILED")}");
        return ok ? 0 : 1;
    }

    // ---- c) server instance destroyed and recreated mid-stream -------------
    private static int CaseC()
    {
        Console.WriteLine("\n=== C) pipe server destroyed and recreated mid-stream (app restart)");
        var pipe = Full("c");

        var failures = 0;
        var pub = new FramePublisher(pipe, W, H, 30);
        pub.Start();

        var feeder = new Thread(() =>
        {
            ulong seq = 0;
            var sw = new Stopwatch();
            while (!Volatile.Read(ref _feederStop))
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                pub.PublishFrame(body, info);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _feederStop = false;
        feeder.Start();

        bool first = TryGetOneFrame(pipe, 3000, out double ms1, out _);
        Console.WriteLine($"  before destroy: frame in {ms1:F1} ms  ok={first}");
        if (!first) failures++;

        // Destroy the server exactly as an app restart would.
        Volatile.Write(ref _feederStop, true);
        feeder.Join();
        pub.Dispose();

        bool duringDown = TryGetOneFrame(pipe, 400, out _, out _);
        Console.WriteLine($"  while destroyed: frame available={duringDown} (expected False)");

        // Recreate on the same pipe name and serve again.
        using var pub2 = new FramePublisher(pipe, W, H, 30);
        pub2.Start();
        var feeder2 = new Thread(() =>
        {
            ulong seq = 0;
            while (!Volatile.Read(ref _feederStop))
            {
                seq++;
                var body = BuildFrame(seq, out var info);
                pub2.PublishFrame(body, info);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _feederStop = false;
        feeder2.Start();

        bool recovered = TryGetOneFrame(pipe, 3000, out double ms2, out string? err2);
        Console.WriteLine($"  after recreate: frame in {ms2:F1} ms  ok={recovered}  error={err2 ?? "none"}");
        if (!recovered) failures++;

        // And a client that dies immediately after connecting must not stop the next one.
        for (int i = 0; i < 20; i++)
        {
            try
            {
                var c = new NamedPipeClientStream(".", Bare(pipe), PipeDirection.In, PipeOptions.Asynchronous);
                c.Connect(1000);
                c.Dispose(); // vanish before reading anything at all
            }
            catch { }
        }
        Thread.Sleep(200);
        bool afterKills = TryGetOneFrame(pipe, 3000, out double ms3, out string? err3);
        Console.WriteLine($"  after 20 kill-before-read clients: frame in {ms3:F1} ms  ok={afterKills}  error={err3 ?? "none"}");
        if (!afterKills) failures++;

        Volatile.Write(ref _feederStop, true);
        feeder2.Join();
        Thread.Sleep(100);
        Console.WriteLine($"  C: {(failures == 0 ? "OK" : "FAILED")}");
        return failures;
    }

    private static bool _feederStop;
    public static int DebugOnly;

    private static bool TryGetOneFrame(string pipe, int timeoutMs, out double ms) =>
        TryGetOneFrame(pipe, timeoutMs, out ms, out _);

    private static bool TryGetOneFrame(string pipe, int timeoutMs, out double ms, out string? error)
    {
        ms = -1;
        error = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new NamedPipeClientStream(".", Bare(pipe), PipeDirection.In, PipeOptions.Asynchronous);
            c.Connect(timeoutMs);
            using var reader = new BinaryReader(c);
            if (!ReadConfig(reader, out _)) { error = "no config"; return false; }
            if (!TryReadFrame(reader, out _, out _)) { error = "no frame"; return false; }
            ms = sw.Elapsed.TotalMilliseconds;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads the 12-byte config header plus its payload (the server sends both on connect).</summary>
    private static bool ReadConfig(BinaryReader reader, out double el)
    {
        el = 0;
        var hdrBytes = ReadExact(reader, 12);
        if (hdrBytes.Length != 12) return false;
        var hdr = MemoryMarshal.Read<WsMsgHeader>(hdrBytes);
        if (hdr.Magic != Protocol.Magic || hdr.Type != Protocol.TypeConfig) return false;
        var payload = ReadExact(reader, (int)hdr.PayloadLen);
        return payload.Length == (int)hdr.PayloadLen;
    }

    // Every read is bounded: a client that stops receiving must fail the cycle,
    // never hang the test (the failure mode we are testing for).
    private const int ReadTimeoutMs = 1500;

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var buf = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = ReadWithTimeout(reader.BaseStream, buf, total, count - total);
            if (n <= 0) return buf[..total];
            total += n;
        }
        return buf;
    }

    private static int ReadWithTimeout(Stream s, byte[] buf, int offset, int count)
    {
        var task = s.ReadAsync(buf, offset, count);
        if (!task.Wait(ReadTimeoutMs))
            throw new TimeoutException($"no data for {ReadTimeoutMs} ms");
        return task.Result;
    }

    private static bool TryReadFrame(BinaryReader reader, out WsFrameInfo info, out byte[] body)
    {
        info = default;
        body = Array.Empty<byte>();
        var hdrBytes = ReadExact(reader, 12);
        if (hdrBytes.Length != 12) return false;
        var hdr = MemoryMarshal.Read<WsMsgHeader>(hdrBytes);
        if (hdr.Magic != Protocol.Magic || hdr.Version != Protocol.Version) return false;
        if (hdr.Type != Protocol.TypeFrame || hdr.PayloadLen < FrameInfoSize) return false;
        var payload = ReadExact(reader, (int)hdr.PayloadLen);
        if (payload.Length != (int)hdr.PayloadLen) return false;
        info = MemoryMarshal.Read<WsFrameInfo>(payload);
        body = payload[FrameInfoSize..];
        return true;
    }
}
