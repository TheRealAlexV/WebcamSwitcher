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
}
