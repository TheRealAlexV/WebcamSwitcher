using System.Diagnostics;
using WebcamSwitcher.Core;

namespace WebcamSwitcher.MfProbe;

/// <summary>
/// A/B for the NV12 → Bgra8 preview downscale: proves the vectorised path is
/// byte-identical to the pre-SIMD scalar reference, then times both on synthetic
/// frames shaped like the real camera output (1280x720 and 1920x1080, grain and
/// flat areas, chroma patches, and the clamping extremes).
/// </summary>
internal static unsafe class PreviewBench
{
    private readonly record struct Case(int SrcW, int SrcH, int DstW, int DstH, string Content);

    public static int Run()
    {
        Console.WriteLine($"PreviewBench: Vector256.IsHardwareAccelerated={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}, Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}, LE={BitConverter.IsLittleEndian}");

        var cases = new List<Case>();
        string[] contents = { "black", "white", "noise", "gradient+grain", "chroma-extremes", "flat-gray" };
        foreach (var content in contents)
        {
            cases.Add(new(1280, 720, 320, 180, content));    // C920 / Osmo
            cases.Add(new(1920, 1080, 320, 180, content));   // 1080p camera
            cases.Add(new(1918, 1078, 320, 180, content));   // odd source (maps clamp)
            cases.Add(new(1280, 720, 319, 179, content));    // odd destination (tail pixels)
            cases.Add(new(1280, 720, 7, 5, content));        // narrower than a vector
        }

        int failures = 0;
        foreach (var c in cases)
        {
            byte[] src = BuildNv12(c.SrcW, c.SrcH, c.Content);
            byte[] simd = new byte[c.DstW * c.DstH * 4];
            byte[] reference = new byte[c.DstW * c.DstH * 4];

            fixed (byte* s = src)
            {
                ColorConverter.Nv12ToBgra8Downscale(s, c.SrcW, c.SrcH, c.SrcW, simd, c.DstW, c.DstH);
                ColorConverter.Nv12ToBgra8DownscaleReference(s, c.SrcW, c.SrcH, c.SrcW, reference, c.DstW, c.DstH);
            }

            int firstDiff = -1, maxDelta = 0;
            for (int i = 0; i < simd.Length; i++)
            {
                int d = Math.Abs(simd[i] - reference[i]);
                if (d != 0)
                {
                    if (firstDiff < 0) firstDiff = i;
                    if (d > maxDelta) maxDelta = d;
                }
            }

            if (firstDiff >= 0) failures++;
            string verdict = firstDiff < 0
                ? "IDENTICAL"
                : $"DIFFERS at byte {firstDiff} (max channel delta {maxDelta})";
            Console.WriteLine($"  {c.SrcW}x{c.SrcH,-4} -> {c.DstW}x{c.DstH,-3} {c.Content,-15} {verdict}");
        }

        Console.WriteLine(failures == 0
            ? "Byte identity: PASS (all cases bit-identical, delta 0)"
            : $"Byte identity: FAIL ({failures} case(s) differ)");

        // Padded source stride (a driver may hand us stride > width).
        foreach (var (w, h, stride) in new[] { (1280, 720, 1344), (1920, 1080, 1984), (1280, 720, 1408) })
        {
            foreach (var content in new[] { "noise", "gradient+grain", "chroma-extremes" })
            {
                byte[] tight = BuildNv12(w, h, content);
                var padded = new byte[stride * h + stride * (h / 2)];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(tight, y * w, padded, y * stride, w);
                for (int y = 0; y < h / 2; y++)
                    Buffer.BlockCopy(tight, w * h + y * w, padded, stride * h + y * stride, w);

                byte[] a = new byte[320 * 180 * 4];
                byte[] b = new byte[320 * 180 * 4];
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(padded, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    byte* s = (byte*)pin.AddrOfPinnedObject();
                    ColorConverter.Nv12ToBgra8Downscale(s, w, h, stride, a, 320, 180);
                    ColorConverter.Nv12ToBgra8DownscaleReference(s, w, h, stride, b, 320, 180);
                }
                finally { pin.Free(); }

                bool same = a.AsSpan().SequenceEqual(b);
                if (!same) failures++;
                Console.WriteLine($"  {w}x{h} stride {stride,-5} {content,-15} {(same ? "IDENTICAL" : "DIFFERS")}");
            }
        }

        // ---- timing --------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("Timing (300 iterations each, same buffer, release build):");
        var maps = BuildMaps(1280, 720, 320, 180);
        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080) })
        {
            const int dstW = 320, dstH = 180, iterations = 300;
            byte[] src = BuildNv12(w, h, "gradient+grain");
            byte[] dst = new byte[dstW * dstH * 4];
            var m = BuildMaps(w, h, dstW, dstH);

            var pin = System.Runtime.InteropServices.GCHandle.Alloc(src, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                byte* s = (byte*)pin.AddrOfPinnedObject();
                double preUs = Time(() => PreSimdDownscale(s, w, h, w, dst, dstW, dstH, m), iterations);
                double simdUs = Time(() => ColorConverter.Nv12ToBgra8Downscale(s, w, h, w, dst, dstW, dstH), iterations);
                Console.WriteLine($"  {w}x{h} -> {dstW}x{dstH}: pre-SIMD scalar {preUs,7:F1} us/frame, vector {simdUs,7:F1} us/frame " +
                                  $"({preUs / simdUs:F2}x, saves {preUs - simdUs:F1} us/frame = {100 * (preUs - simdUs) / preUs:F0}%)");
            }
            finally
            {
                pin.Free();
            }
        }

        // Costs that surround the conversion, for the per-slot budget.
        byte[] frame = new byte[1280 * 720 * 3 / 2];
        byte[] copyDst = new byte[frame.Length];
        double copyUs = Time(() => Buffer.BlockCopy(frame, 0, copyDst, 0, frame.Length), 300);
        Console.WriteLine($"  reference: one 1.38 MB BlockCopy {copyUs:F1} us (already removed from FramePublisher)");

        return failures == 0 ? 0 : 1;
    }

    private static double Time(Action body, int iterations)
    {
        for (int i = 0; i < 20; i++) body();      // warm up JIT + maps
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;
    }

    private static (int[] X, int[] UvX, int[] Y, int[] UvY) BuildMaps(int srcW, int srcH, int dstW, int dstH)
    {
        var x = new int[dstW];
        var uvx = new int[dstW];
        for (int i = 0; i < dstW; i++)
        {
            int sx = Math.Min(srcW - 1, i * srcW / dstW);
            x[i] = sx;
            uvx[i] = (sx / 2) * 2;
        }

        var y = new int[dstH];
        var uvy = new int[dstH];
        for (int i = 0; i < dstH; i++)
        {
            int sy = Math.Min(srcH - 1, i * srcH / dstH);
            y[i] = sy;
            uvy[i] = (sy / 2) * (srcW / 2) * 2;
        }

        return (x, uvx, y, uvy);
    }

    /// <summary>
    /// Verbatim copy of the pre-SIMD shipped implementation (per-thread maps, one
    /// pixel at a time), used only to time the A/B on this machine.
    /// </summary>
    private static unsafe void PreSimdDownscale(
        byte* src, int srcW, int srcH, int srcStride, byte[] dst, int dstW, int dstH,
        (int[] X, int[] UvX, int[] Y, int[] UvY) map)
    {
        int[] mapX = map.X, mapUvX = map.UvX, mapY = map.Y;
        byte* yPlane = src;
        byte* uvPlane = src + srcStride * srcH;
        fixed (byte* dp = dst)
        {
            for (int y = 0; y < dstH; y++)
            {
                byte* srow = yPlane + mapY[y] * srcStride;
                byte* uvrow = uvPlane + mapY[y] / 2 * srcStride;
                byte* drow = dp + y * dstW * 4;
                for (int x = 0; x < dstW; x++)
                {
                    int yy = srow[mapX[x]];
                    int u = uvrow[mapUvX[x]] - 128;
                    int v = uvrow[mapUvX[x] + 1] - 128;
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

    /// <summary>Builds one NV12 frame with the requested synthetic content.</summary>
    private static byte[] BuildNv12(int w, int h, string content)
    {
        var buf = new byte[w * h * 3 / 2];
        var rng = new Random(w * 31 + h * 17 + content.Length);
        int ySize = w * h;

        switch (content)
        {
            case "black":
                for (int i = 0; i < ySize; i++) buf[i] = 16;
                for (int i = ySize; i < buf.Length; i++) buf[i] = 128;
                break;
            case "white":
                for (int i = 0; i < ySize; i++) buf[i] = 235;
                for (int i = ySize; i < buf.Length; i++) buf[i] = 128;
                break;
            case "flat-gray":
                for (int i = 0; i < ySize; i++) buf[i] = 128;
                for (int i = ySize; i < buf.Length; i++) buf[i] = (byte)(i % 2 == 0 ? 90 : 160);
                break;
            case "noise":
                rng.NextBytes(buf);
                break;
            case "chroma-extremes":
                for (int i = 0; i < ySize; i++) buf[i] = (byte)(i % 256);
                for (int i = ySize; i < buf.Length; i += 2)
                {
                    buf[i] = (byte)((i / 2) % 2 == 0 ? 0 : 255);          // U extremes
                    buf[i + 1] = (byte)((i / 4) % 2 == 0 ? 255 : 0);      // V extremes
                }
                break;
            default: // gradient+grain: smooth luma plus per-pixel noise, chroma blocks
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                        buf[row + x] = (byte)Math.Clamp((x * 200 / Math.Max(1, w)) + (y * 40 / Math.Max(1, h)) + rng.Next(-12, 13), 0, 255);
                }
                for (int y = 0; y < h / 2; y++)
                {
                    int row = ySize + y * w;
                    for (int x = 0; x < w; x += 2)
                    {
                        byte u = (byte)Math.Clamp(128 + (x - w / 2) * 60 / Math.Max(1, w) + rng.Next(-6, 7), 0, 255);
                        byte v = (byte)Math.Clamp(128 + (y - h / 4) * 60 / Math.Max(1, h) + rng.Next(-6, 7), 0, 255);
                        buf[row + x] = u;
                        buf[row + x + 1] = v;
                    }
                }
                break;
        }

        return buf;
    }
}
