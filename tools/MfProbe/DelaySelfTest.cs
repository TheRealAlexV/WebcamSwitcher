using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Exercises <see cref="FrameDelayBuffer"/> without cameras, pipes or threads of
/// its own: depth-0 bypass and its zero allocation, the filling ramp (newest
/// frame out, never "no frame"), strict FIFO once full, live resize up and down,
/// the memory bound, and a concurrent configure/push smoke test.
/// </summary>
internal static class DelaySelfTest
{
    private const int FrameBytes = 16;
    private const byte Marker = 0x42;

    public static int Run()
    {
        bool ok = true;
        ok &= BypassCostsNothing();
        ok &= FillRampEmitsNewest();
        ok &= StrictFifo();
        ok &= ResizeUpRampsWithoutStalling();
        ok &= ResizeDownKeepsOrder();
        ok &= LongRunKeepsOrderAndBound();
        ok &= DisableReleasesMemory();
        ok &= FrameSizeChangeResets();
        ok &= ConcurrentConfigureIsSafe();

        Console.WriteLine(ok
            ? "RESULT: PASS (delay buffer contract holds)"
            : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Depth 0 must publish the arriving frame and allocate nothing.</summary>
    private static bool BypassCostsNothing()
    {
        Console.WriteLine("depth 0 bypass:");
        var buffer = new FrameDelayBuffer();
        bool ok = Expect("default depth", buffer.Depth, 0);

        byte[] frame = Frame(1);
        // Warm up so JIT tier-up cannot pollute the allocation measurement.
        for (int i = 0; i < 5000; i++)
            buffer.TryPush(frame, out _);

        bool delayed = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            delayed |= buffer.TryPush(frame, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        ok &= Expect("never delays", delayed, false);
        ok &= Expect("allocated bytes", allocated, 0L);
        ok &= Expect("bytes held", buffer.BytesHeld, 0L);
        ok &= Expect("buffered frames", buffer.Buffered, 0);
        return ok;
    }

    /// <summary>While filling, the newest frame is published and nothing is held back.</summary>
    private static bool FillRampEmitsNewest()
    {
        Console.WriteLine("fill ramp:");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(3);

        bool ok = Expect("depth", buffer.Depth, 3);
        for (int id = 1; id <= 3; id++)
        {
            bool delayed = Push(buffer, id, out int published);
            ok &= Expect($"push {id}: not delayed", delayed, false);
            ok &= Expect($"push {id}: publishes newest", published, id);
            ok &= Expect($"push {id}: buffered", buffer.Buffered, id);
        }

        // Full from here on: the frame from three frames ago comes out, in order.
        for (int i = 0; i < 3; i++)
        {
            int id = 4 + i;
            bool delayed = Push(buffer, id, out int published);
            ok &= Expect($"push {id}: delayed", delayed, true);
            ok &= Expect($"push {id}: publishes oldest", published, 1 + i);
        }
        ok &= Expect("stays full", buffer.Buffered, 3);
        return ok;
    }

    /// <summary>Once full, output is exactly the input, delayed by Depth, no loss or duplicate.</summary>
    private static bool StrictFifo()
    {
        Console.WriteLine("strict FIFO (depth 4, 100 frames):");
        const int depth = 4, frames = 100;
        var buffer = new FrameDelayBuffer();
        buffer.Configure(depth);

        bool ok = true;
        for (int id = 1; id <= frames; id++)
        {
            bool delayed = Push(buffer, id, out int published);
            int expected = id - depth;
            if (expected < 1)
                ok &= Expect($"push {id}: filling", delayed, false);
            else
                ok &= Expect($"push {id}: output", published, expected);
        }
        ok &= Expect("buffered", buffer.Buffered, depth);
        return ok;
    }

    /// <summary>
    /// Raising the depth mid-stream must not stall: the ring behaves like a fresh
    /// fill and then resumes with the oldest frame it still holds.
    /// </summary>
    private static bool ResizeUpRampsWithoutStalling()
    {
        Console.WriteLine("resize up (2 -> 4):");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(2);
        Push(buffer, 1, out _);
        Push(buffer, 2, out _);
        Push(buffer, 3, out int third);          // full -> 1 out, holds 2,3
        bool ok = Expect("push 3: output", third, 1);

        buffer.Configure(4);
        ok &= Expect("grow keeps held frames", buffer.Buffered, 2);
        ok &= Expect("grow keeps depth", buffer.Depth, 4);

        // Ramp again: 4 and 5 go out immediately instead of nothing.
        foreach (int id in new[] { 4, 5 })
        {
            bool delayed = Push(buffer, id, out int published);
            ok &= Expect($"push {id}: not delayed", delayed, false);
            ok &= Expect($"push {id}: publishes newest", published, id);
        }

        // Full again: it resumes at 2, the oldest frame still held, and advances
        // one per frame. The ramp repeated 4 and 5, but nothing was skipped and
        // the queue itself never reordered.
        foreach (int expected in new[] { 2, 3, 4, 5 })
        {
            int id = expected + 4;
            bool delayed = Push(buffer, id, out int published);
            ok &= Expect($"push {id}: delayed", delayed, true);
            ok &= Expect($"push {id}: output", published, expected);
        }
        return ok;
    }

    /// <summary>
    /// Lowering the depth mid-stream keeps the oldest frames, drops the newest
    /// and keeps counting forward: no duplicate, no reorder, no stall.
    /// </summary>
    private static bool ResizeDownKeepsOrder()
    {
        Console.WriteLine("resize down (5 -> 2):");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(5);
        bool ok = true;
        for (int id = 1; id <= 7; id++)
        {
            bool delayed = Push(buffer, id, out int published);
            if (id <= 5)
                ok &= Expect($"push {id}: filling", delayed, false);
            else
                ok &= Expect($"push {id}: output", published, id - 5);
        }

        buffer.Configure(2);
        ok &= Expect("shrink keeps oldest", buffer.Buffered, 2);
        ok &= Expect("shrink keeps depth", buffer.Depth, 2);

        int[] expected = { 3, 4, 8, 9, 10 };
        for (int i = 0; i < expected.Length; i++)
        {
            int id = 8 + i;
            bool delayed = Push(buffer, id, out int published);
            ok &= Expect($"push {id}: delayed", delayed, true);
            ok &= Expect($"push {id}: output", published, expected[i]);
        }
        return ok;
    }

    /// <summary>
    /// A long run at a steady depth: output is contiguous, every frame exactly
    /// once, and the ring never holds more than depth × frame bytes.
    /// </summary>
    private static bool LongRunKeepsOrderAndBound()
    {
        Console.WriteLine("long run (depth 10, 2000 frames):");
        const int depth = 10, frames = 2000;
        var buffer = new FrameDelayBuffer();
        buffer.Configure(depth);

        bool ok = true;
        int last = 0;
        for (int id = 1; id <= frames; id++)
        {
            bool delayed = Push(buffer, id, out int published);
            if (id <= depth)
            {
                ok &= Expect($"push {id}: filling", delayed, false);
                continue;
            }
            if (published != last + 1)
            {
                ok &= Expect($"push {id}: contiguous output", published, last + 1);
                ok = false;
            }
            last = published;
            if (buffer.BytesHeld > (long)depth * FrameBytes)
            {
                ok &= Expect($"push {id}: memory bound", buffer.BytesHeld, (long)depth * FrameBytes);
                ok = false;
            }
        }
        ok &= Expect("final output", last, frames - depth);
        ok &= Expect("final buffered", buffer.Buffered, depth);
        ok &= Expect("final bytes held", buffer.BytesHeld, (long)depth * FrameBytes);
        return ok;
    }

    /// <summary>Configure(0) at runtime returns every buffer and restores the bypass.</summary>
    private static bool DisableReleasesMemory()
    {
        Console.WriteLine("disable releases memory:");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(10);
        for (int id = 1; id <= 20; id++)
            Push(buffer, id, out _);
        bool ok = Expect("held while enabled", buffer.BytesHeld, 10L * FrameBytes);

        buffer.Configure(0);
        ok &= Expect("depth", buffer.Depth, 0);
        ok &= Expect("buffered", buffer.Buffered, 0);
        ok &= Expect("bytes held", buffer.BytesHeld, 0L);

        bool delayed = Push(buffer, 21, out int published);
        ok &= Expect("bypass after disable", delayed, false);
        ok &= Expect("publishes newest", published, 21);
        return ok;
    }

    /// <summary>A resolution change cannot mix frame sizes in one ring.</summary>
    private static bool FrameSizeChangeResets()
    {
        Console.WriteLine("frame size change:");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(3);
        for (int id = 1; id <= 3; id++)
            Push(buffer, id, out _);

        var big = new byte[FrameBytes * 2];
        BitConverter.TryWriteBytes(big.AsSpan(1), 99);
        bool delayed = buffer.TryPush(big, out byte[]? frame);
        bool ok = Expect("ring restarts", delayed, false);
        ok &= Expect("holds one frame", buffer.Buffered, 1);
        ok &= Expect("reset counted", buffer.Resets, 1);
        ok &= Expect("bytes held", buffer.BytesHeld, (long)FrameBytes * 2);
        ok &= Expect("returned null", frame != null, false);
        return ok;
    }

    /// <summary>
    /// Configure racing pushes (the settings window applying a delay while
    /// capture is running) must never throw and never emit a torn frame.
    /// </summary>
    private static bool ConcurrentConfigureIsSafe()
    {
        Console.WriteLine("concurrent configure:");
        var buffer = new FrameDelayBuffer();
        buffer.Configure(5);
        int errors = 0, torn = 0;
        var stop = DateTime.UtcNow.AddMilliseconds(300);

        var producer = new Thread(() =>
        {
            try
            {
                for (int id = 1; DateTime.UtcNow < stop; id++)
                {
                    byte[] frame = Frame(id);
                    bool delayed = buffer.TryPush(frame, out byte[]? outFrame);
                    var span = (delayed ? outFrame! : frame).AsSpan();
                    // Whatever comes out must be one of our frames, intact: the
                    // marker present, the id duplicated in the payload equal, and
                    // the id within the range already pushed.
                    int a = BitConverter.ToInt32(span[1..]);
                    int b = BitConverter.ToInt32(span[5..]);
                    if (span[0] != Marker || a != b || a < 1 || a > id)
                        Interlocked.Increment(ref torn);
                }
            }
            catch { Interlocked.Increment(ref errors); }
        });
        var configurer = new Thread(() =>
        {
            try
            {
                int[] depths = { 0, 3, 60, 1, 8 };
                for (int i = 0; DateTime.UtcNow < stop; i++)
                {
                    buffer.Configure(depths[i % depths.Length]);
                    if (buffer.Depth < 0 || buffer.Depth > FrameDelayBuffer.MaxDepth)
                        Interlocked.Increment(ref errors);
                }
            }
            catch { Interlocked.Increment(ref errors); }
        });

        producer.Start();
        configurer.Start();
        producer.Join();
        configurer.Join();

        bool ok = Expect("exceptions", errors, 0);
        ok &= Expect("torn frames", torn, 0);
        buffer.Configure(0);
        ok &= Expect("released", buffer.BytesHeld, 0L);
        return ok;
    }

    private static bool Push(FrameDelayBuffer buffer, int id, out int published)
    {
        byte[] frame = Frame(id);
        bool delayed = buffer.TryPush(frame, out byte[]? outFrame);
        published = delayed ? BitConverter.ToInt32(outFrame!.AsSpan(1)) : id;
        return delayed;
    }

    /// <summary>
    /// Frame whose payload carries its own id twice, so ordering and integrity
    /// are both checkable.
    /// </summary>
    private static byte[] Frame(int id)
    {
        var frame = new byte[FrameBytes];
        frame[0] = Marker;
        BitConverter.TryWriteBytes(frame.AsSpan(1), id);
        BitConverter.TryWriteBytes(frame.AsSpan(5), id);
        return frame;
    }

    private static bool Expect<T>(string what, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Console.WriteLine($"  {what,-45} {(ok ? "OK" : $"MISMATCH (got {actual}, want {expected})")}");
        return ok;
    }
}
