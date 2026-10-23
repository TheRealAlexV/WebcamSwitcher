using System.Reflection;
using Vortice.MediaFoundation;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Camera-free check of the sample → tight NV12 copy used by MfMjpegSource.
/// It builds real MF samples with known plane content at three strides (tight,
/// padded, bottom-up) and calls the same private CopySample the read loop uses,
/// then verifies the published byte layout byte for byte. This covers the
/// geometry risk of the NV12 fast path without needing a free camera.
/// </summary>
internal static class CopyPathSelfTest
{
    private const int W = 64;
    private const int H = 48; // small on purpose; the code path is size independent

    public static int Run()
    {
        Console.WriteLine($"CopyPathSelfTest: {W}x{H} NV12, expected tight size {W * H * 3 / 2}");

        MediaFactory.MFStartup(false);
        try
        {
            int failures = 0;
            failures += Check("tight   stride=+W", W);
            failures += Check("padded  stride=+W+32", W + 32);
            failures += Check("padded  stride=+W+192", W + 192);
            failures += Check("bottom-up stride=-W", -W);
            failures += Check("bottom-up stride=-(W+32)", -(W + 32));

            Console.WriteLine(failures == 0
                ? "RESULT: PASS (all strides produce identical tight NV12)"
                : $"RESULT: FAIL ({failures} case(s))");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static int Check(string label, int stride)
    {
        int abs = Math.Abs(stride);
        bool bottomUp = stride < 0;
        int uvH = H / 2;

        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(abs * H + abs * uvH);
        buffer.Lock(out IntPtr p, out _, out _);
        unsafe
        {
            byte* basePtr = (byte*)p;
            for (int y = 0; y < H; y++)
            {
                // Stored rows are always top-down in memory; bottom-up means the
                // visible row order is reversed, which is what the copy must undo.
                int storeRow = bottomUp ? H - 1 - y : y;
                byte* row = basePtr + storeRow * abs;
                for (int x = 0; x < abs; x++)
                    row[x] = (byte)((y * 7 + x) & 0xFF);
            }
            byte* uv = basePtr + abs * H;
            for (int y = 0; y < uvH; y++)
            {
                int storeRow = bottomUp ? uvH - 1 - y : y;
                byte* row = uv + storeRow * abs;
                for (int x = 0; x < abs; x += 2)
                {
                    row[x] = (byte)(40 + y);
                    row[x + 1] = (byte)(200 - y);
                }
            }
        }
        buffer.Unlock();
        buffer.CurrentLength = abs * H + abs * uvH;
        sample.AddBuffer(buffer);

        // Drive the real code: set the private _stride/_frame fields, call CopySample.
        using var src = new MfMjpegSource("copytest", label, W, H);
        var t = typeof(MfMjpegSource);
        var frame = new byte[Protocol.Nv12Size(W, H)];
        t.GetField("_stride", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(src, stride);
        t.GetField("_frame", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(src, frame);

        var copy = t.GetMethod("CopySample", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool ok = (bool)copy.Invoke(src, new object[] { sample })!;
        if (!ok)
        {
            Console.WriteLine($"  {label}: FAIL CopySample returned false");
            return 1;
        }

        int uvy = W * H;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (frame[y * W + x] != (byte)((y * 7 + x) & 0xFF))
                {
                    Console.WriteLine($"  {label}: FAIL Y mismatch at ({x},{y}): {frame[y * W + x]} != {(byte)((y * 7 + x) & 0xFF)}");
                    return 1;
                }
            }
        }
        for (int y = 0; y < uvH; y++)
        {
            for (int x = 0; x < W; x += 2)
            {
                byte wantU = (byte)(40 + y);
                byte wantV = (byte)(200 - y);
                if (frame[uvy + y * W + x] != wantU || frame[uvy + y * W + x + 1] != wantV)
                {
                    Console.WriteLine($"  {label}: FAIL UV mismatch at ({x},{y})");
                    return 1;
                }
            }
        }

        Console.WriteLine($"  {label}: OK (sample bytes {src.LastSampleBytes}, tight {frame.Length})");
        return 0;
    }
}
