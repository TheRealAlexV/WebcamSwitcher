using System.Diagnostics;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// Microbenchmark for the per-frame work this pass touched: the 320x180 preview
/// downscale (old per-pixel-divide loop vs the shipped precomputed-map version)
/// and one 1.38 MB frame copy (the copy FramePublisher no longer performs).
/// Runs fully offline on synthetic data; it measures this machine's CPU cost
/// per frame, which is what the app pays per camera per frame.
/// </summary>
internal static class ConvBench
{
    public static int Run()
    {
        const int W = 1280, H = 720;
        const int PW = 320;
        const int PH = 180;
        const int iterations = 300;

        var nv12 = new byte[Protocol.Nv12Size(W, H)];
        var rng = new Random(1234);
        rng.NextBytes(nv12);
        var preview = new byte[PW * PH * 4];

        // Warm up (JIT + first-time map build).
        unsafe
        {
            fixed (byte* src = nv12)
                ColorConverter.Nv12ToBgra8Downscale(src, W, H, W, preview, PW, PH);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            unsafe
            {
                fixed (byte* src = nv12)
                    ColorConverter.Nv12ToBgra8Downscale(src, W, H, W, preview, PW, PH);
            }
        }
        sw.Stop();
        double newUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;

        sw.Restart();
        for (int i = 0; i < iterations; i++)
            OldDownscale(nv12, W, H, W, preview, PW, PH);
        sw.Stop();
        double oldUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;

        // One full-frame copy, the amount removed from FramePublisher per publish.
        var src1 = new byte[Protocol.Nv12Size(W, H)];
        var dst1 = new byte[Protocol.Nv12Size(W, H)];
        sw.Restart();
        for (int i = 0; i < iterations; i++)
            Buffer.BlockCopy(src1, 0, dst1, 0, src1.Length);
        sw.Stop();
        double copyUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;

        Console.WriteLine($"ConvBench: {W}x{H} NV12 -> {PW}x{PH} Bgra8, {iterations} iterations");
        Console.WriteLine($"  old per-pixel-divide loop: {oldUs,7:F1} us/frame");
        Console.WriteLine($"  shipped map-based loop:    {newUs,7:F1} us/frame  ({(oldUs / newUs):F2}x faster, saves {oldUs - newUs:F1} us)");
        Console.WriteLine($"  one 1.38 MB BlockCopy:     {copyUs,7:F1} us  (removed from FramePublisher per publish)");
        Console.WriteLine($"  expected saving per slot per frame: {(oldUs - newUs + copyUs):F1} us (preview enabled) / {copyUs:F1} us (previews disabled)");
        return 0;
    }

    /// <summary>Verbatim copy of the pre-pass implementation, for the comparison.</summary>
    private static unsafe void OldDownscale(byte[] srcBytes, int srcW, int srcH, int srcStride, byte[] dst, int dstW, int dstH)
    {
        fixed (byte* src = srcBytes)
        fixed (byte* dp = dst)
        {
            byte* yPlane = src;
            byte* uvPlane = src + srcStride * srcH;
            for (int y = 0; y < dstH; y++)
            {
                int sy = Math.Min(srcH - 1, y * srcH / dstH);
                byte* srow = yPlane + sy * srcStride;
                byte* uvrow = uvPlane + (sy / 2) * srcStride;
                byte* drow = dp + y * dstW * 4;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = Math.Min(srcW - 1, x * srcW / dstW);
                    int yy = srow[sx];
                    int u = uvrow[(sx / 2) * 2] - 128;
                    int v = uvrow[(sx / 2) * 2 + 1] - 128;
                    int c = yy - 16;
                    int r = (298 * c + 409 * v + 128) >> 8;
                    int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                    int b = (298 * c + 516 * u + 128) >> 8;
                    byte* o = drow + x * 4;
                    o[0] = (byte)Math.Clamp(b, 0, 255);
                    o[1] = (byte)Math.Clamp(g, 0, 255);
                    o[2] = (byte)Math.Clamp(r, 0, 255);
                    o[3] = 255;
                }
            }
        }
    }
}
