using System.Runtime.Intrinsics;

namespace WebcamSwitcher.Core;

/// <summary>
/// Software color-space conversion + scaling. Converts tightly-strided Bgra8
/// source pixels into NV12 at the target resolution (bilinear upscale, BT.601
/// full range).
/// </summary>
public static class ColorConverter
{
    public static unsafe void Bgra8ToNv12(
        byte* src, int srcW, int srcH, int srcStride,
        byte[] dstNv12, int dstW, int dstH)
    {
        int dstYSize = dstW * dstH;
        fixed (byte* dstPtr = dstNv12)
        {
            byte* dstY = dstPtr;
            byte* dstUV = dstPtr + dstYSize;

            float xScale = (float)srcW / dstW;
            float yScale = (float)srcH / dstH;

            // ---- Y plane (bilinear) ----
            for (int y = 0; y < dstH; y++)
            {
                float sy = (y + 0.5f) * yScale - 0.5f;
                int y0 = Math.Max(0, (int)MathF.Floor(sy));
                int y1 = Math.Min(srcH - 1, y0 + 1);
                float fy = Math.Clamp(sy - y0, 0f, 1f);

                byte* row0 = src + y0 * srcStride;
                byte* row1 = src + y1 * srcStride;
                byte* d = dstY + y * dstW;

                for (int x = 0; x < dstW; x++)
                {
                    float sx = (x + 0.5f) * xScale - 0.5f;
                    int x0 = Math.Max(0, (int)MathF.Floor(sx));
                    int x1 = Math.Min(srcW - 1, x0 + 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f);

                    byte* p00 = row0 + x0 * 4;
                    byte* p10 = row0 + x1 * 4;
                    byte* p01 = row1 + x0 * 4;
                    byte* p11 = row1 + x1 * 4;

                    float b = Bilinear(p00[0], p10[0], p01[0], p11[0], fx, fy);
                    float g = Bilinear(p00[1], p10[1], p01[1], p11[1], fx, fy);
                    float r = Bilinear(p00[2], p10[2], p01[2], p11[2], fx, fy);

                    float yy = 0.299f * r + 0.587f * g + 0.114f * b;
                    d[x] = (byte)Math.Clamp((int)(yy + 0.5f), 0, 255);
                }
            }

            // ---- UV plane (bilinear at 2x2 block centers) ----
            int uvW = dstW / 2;
            int uvH = dstH / 2;
            for (int y = 0; y < uvH; y++)
            {
                float cy = y * 2 + 1.5f;
                float sy = cy * yScale - 0.5f;
                int y0 = Math.Max(0, (int)MathF.Floor(sy));
                int y1 = Math.Min(srcH - 1, y0 + 1);
                float fy = Math.Clamp(sy - y0, 0f, 1f);

                byte* row0 = src + y0 * srcStride;
                byte* row1 = src + y1 * srcStride;
                byte* d = dstUV + y * (uvW * 2);

                for (int x = 0; x < uvW; x++)
                {
                    float cx = x * 2 + 1.5f;
                    float sx = cx * xScale - 0.5f;
                    int x0 = Math.Max(0, (int)MathF.Floor(sx));
                    int x1 = Math.Min(srcW - 1, x0 + 1);
                    float fx = Math.Clamp(sx - x0, 0f, 1f);

                    byte* p00 = row0 + x0 * 4;
                    byte* p10 = row0 + x1 * 4;
                    byte* p01 = row1 + x0 * 4;
                    byte* p11 = row1 + x1 * 4;

                    float b = Bilinear(p00[0], p10[0], p01[0], p11[0], fx, fy);
                    float g = Bilinear(p00[1], p10[1], p01[1], p11[1], fx, fy);
                    float r = Bilinear(p00[2], p10[2], p01[2], p11[2], fx, fy);

                    float u = -0.169f * r - 0.331f * g + 0.500f * b + 128f;
                    float v = 0.500f * r - 0.419f * g - 0.081f * b + 128f;

                    d[x * 2] = (byte)Math.Clamp((int)(u + 0.5f), 0, 255);
                    d[x * 2 + 1] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
                }
            }
        }
    }

    private static float Bilinear(float a, float b, float c, float d, float fx, float fy)
    {
        float top = a + (b - a) * fx;
        float bot = c + (d - c) * fx;
        return top + (bot - top) * fy;
    }

    /// <summary>Nearest-neighbor downscale of Bgra8 pixels (for UI previews).</summary>
    public static unsafe void Bgra8Downscale(
        byte* src, int srcW, int srcH, int srcStride,
        byte[] dst, int dstW, int dstH)
    {
        fixed (byte* dstPtr = dst)
        {
            for (int y = 0; y < dstH; y++)
            {
                int sy = Math.Min(srcH - 1, y * srcH / dstH);
                byte* srow = src + sy * srcStride;
                byte* drow = dstPtr + y * dstW * 4;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = Math.Min(srcW - 1, x * srcW / dstW);
                    byte* sp = srow + sx * 4;
                    byte* dp = drow + x * 4;
                    dp[0] = sp[0];
                    dp[1] = sp[1];
                    dp[2] = sp[2];
                    dp[3] = 255;
                }
            }
        }
    }

    /// <summary>Nearest-neighbor NV12 → Bgra8 downscale (for UI previews).</summary>
    /// <remarks>
    /// The source coordinates are a pure function of the geometry, so they are
    /// resolved once per (srcW,srcH,dstW,dstH) per thread instead of doing an
    /// integer divide (and a chroma divide) for every output pixel. The colour
    /// math runs eight pixels at a time in <see cref="Vector256{T}"/> lanes; the
    /// arithmetic is the same integer expression as the scalar loop, so the
    /// output is byte-identical (verified by the tools/MfProbe harness).
    /// </remarks>
    public static unsafe void Nv12ToBgra8Downscale(
        byte* src, int srcW, int srcH, int srcStride,
        byte[] dst, int dstW, int dstH)
    {
        EnsureDownscaleMaps(srcW, srcH, dstW, dstH);
        int[] mapX = _mapX!, mapUvX = _mapUvX!, mapY = _mapY!, mapUvY = _mapUvY!;

        byte* yPlane = src;
        byte* uvPlane = src + srcStride * srcH;

        // The packed write below (B | G<<8 | R<<16 | 0xFF000000 stored as an int)
        // matches the byte order only on little-endian targets. Every Windows
        // architecture is little-endian, but fall back rather than assume.
        if (!BitConverter.IsLittleEndian || dstW < 8)
        {
            Nv12ToBgra8DownscaleReference(src, srcW, srcH, srcStride, dst, dstW, dstH);
            return;
        }

        fixed (byte* dp = dst)
        fixed (int* pMapX = mapX)
        fixed (int* pMapUvX = mapUvX)
        {
            Vector256<int> vyOffset = Vector256.Create(16);
            Vector256<int> vyScale = Vector256.Create(298);
            Vector256<int> vRound = Vector256.Create(128);
            Vector256<int> vuScaleG = Vector256.Create(-100);
            Vector256<int> vuScaleB = Vector256.Create(516);
            Vector256<int> vvScaleR = Vector256.Create(409);
            Vector256<int> vvScaleG = Vector256.Create(-208);
            Vector256<int> vMin = Vector256<int>.Zero;
            Vector256<int> vMax = Vector256.Create(255);
            Vector256<int> vAlpha = Vector256.Create(unchecked((int)0xFF000000));
            Vector256<int> vChromaBias = Vector256.Create(128);

            for (int y = 0; y < dstH; y++)
            {
                byte* srow = yPlane + mapY[y] * srcStride;
                byte* uvrow = uvPlane + mapUvY[y] * srcStride;
                byte* drow = dp + y * dstW * 4;
                int x = 0;

                for (; x <= dstW - 8; x += 8)
                {
                    // Gather: eight luma samples and eight U/V pairs. The maps are
                    // shared with the scalar path, so both produce the same pixels.
                    Vector256<int> vy = Vector256.Create(
                        srow[pMapX[x + 0]], srow[pMapX[x + 1]], srow[pMapX[x + 2]], srow[pMapX[x + 3]],
                        srow[pMapX[x + 4]], srow[pMapX[x + 5]], srow[pMapX[x + 6]], srow[pMapX[x + 7]]);
                    Vector256<int> vu = Vector256.Create(
                        uvrow[pMapUvX[x + 0]], uvrow[pMapUvX[x + 1]], uvrow[pMapUvX[x + 2]], uvrow[pMapUvX[x + 3]],
                        uvrow[pMapUvX[x + 4]], uvrow[pMapUvX[x + 5]], uvrow[pMapUvX[x + 6]], uvrow[pMapUvX[x + 7]]);
                    Vector256<int> vv = Vector256.Create(
                        uvrow[pMapUvX[x + 0] + 1], uvrow[pMapUvX[x + 1] + 1], uvrow[pMapUvX[x + 2] + 1], uvrow[pMapUvX[x + 3] + 1],
                        uvrow[pMapUvX[x + 4] + 1], uvrow[pMapUvX[x + 5] + 1], uvrow[pMapUvX[x + 6] + 1], uvrow[pMapUvX[x + 7] + 1]);

                    vu -= vChromaBias;
                    vv -= vChromaBias;
                    Vector256<int> c = (vy - vyOffset) * vyScale + vRound;

                    Vector256<int> vr = (c + vv * vvScaleR) >> 8;
                    Vector256<int> vg = (c + vu * vuScaleG + vv * vvScaleG) >> 8;
                    Vector256<int> vb = (c + vu * vuScaleB) >> 8;

                    vr = Vector256.Min(Vector256.Max(vr, vMin), vMax);
                    vg = Vector256.Min(Vector256.Max(vg, vMin), vMax);
                    vb = Vector256.Min(Vector256.Max(vb, vMin), vMax);

                    // Little-endian int: byte0=B, byte1=G, byte2=R, byte3=255.
                    Vector256<int> packed = vb | (vg << 8) | (vr << 16) | vAlpha;
                    packed.Store((int*)(drow + x * 4));
                }

                for (; x < dstW; x++)
                {
                    int yy = srow[pMapX[x]];
                    int u = uvrow[pMapUvX[x]] - 128;
                    int v = uvrow[pMapUvX[x] + 1] - 128;
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

    /// <summary>
    /// Reference (scalar, pre-SIMD) implementation of
    /// <see cref="Nv12ToBgra8Downscale"/>. Kept as the byte-identity oracle for the
    /// tools/MfProbe harness and used directly on non-x86 targets.
    /// </summary>
    internal static unsafe void Nv12ToBgra8DownscaleReference(
        byte* src, int srcW, int srcH, int srcStride,
        byte[] dst, int dstW, int dstH)
    {
        EnsureDownscaleMaps(srcW, srcH, dstW, dstH);
        Nv12ToBgra8DownscaleScalar(
            src, src + srcStride * srcH,
            _mapX!, _mapUvX!, _mapY!, _mapUvY!,
            dst, srcStride, dstW, dstH);
    }

    /// <summary>
    /// Reference implementation of <see cref="Nv12ToBgra8Downscale"/>: one pixel at
    /// a time, no vector types.
    /// </summary>
    internal static unsafe void Nv12ToBgra8DownscaleScalar(
        byte* yPlane, byte* uvPlane, int[] mapX, int[] mapUvX, int[] mapY, int[] mapUvY,
        byte[] dst, int srcStride, int dstW, int dstH)
    {
        fixed (byte* dp = dst)
        {
            for (int y = 0; y < dstH; y++)
            {
                byte* srow = yPlane + mapY[y] * srcStride;
                byte* uvrow = uvPlane + mapUvY[y] * srcStride;
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

    // Per-thread geometry maps for the preview downscale. Each capture thread has
    // a fixed source size, so this is one array set per thread per geometry.
    [ThreadStatic] private static int _mapKey = -1;
    [ThreadStatic] private static int[]? _mapX;
    [ThreadStatic] private static int[]? _mapUvX;
    [ThreadStatic] private static int[]? _mapY;
    [ThreadStatic] private static int[]? _mapUvY;

    private static void EnsureDownscaleMaps(int srcW, int srcH, int dstW, int dstH)
    {
        int key = HashCode.Combine(srcW, srcH, dstW, dstH);
        if (_mapKey == key && _mapX != null && _mapY != null
            && _mapX.Length == dstW && _mapY.Length == dstH)
            return;

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
            uvy[i] = sy / 2;
        }

        _mapX = x;
        _mapUvX = uvx;
        _mapY = y;
        _mapUvY = uvy;
        _mapKey = key;
    }
}
